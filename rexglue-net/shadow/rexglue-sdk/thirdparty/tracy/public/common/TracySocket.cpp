// REFII: IN-PROCESS TRANSPORT — see TracySocket.hpp.
//
// One listener (the profiler client's) and any number of connections to it, each a pair of
// byte pipes. The contract mirrors the TCP calls it replaces, because both ends of Tracy were
// written against them:
//   Recv     -> bytes when there are some, 0 at end of stream (peer closed, pipe drained),
//               -1 when the timeout passes with nothing to read (poll timing out);
//   HasData  -> true when bytes are waiting OR the peer has closed, as poll reports POLLIN at
//               EOF, so the client reads, sees the end, and goes back to waiting for a profiler;
//   Send     -> blocks while the peer's pipe is full, as a full socket buffer blocks; -1 once
//               either side has closed.
// Connect ignores the address and port: in-process there is exactly one client to reach.

#include <assert.h>
#include <string.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <new>
#include <vector>

#include "TracyAlloc.hpp"
#include "TracySocket.hpp"

namespace tracy
{

namespace
{

// Per direction. Large enough that a frame's worth of compressed events never waits on the
// reader; small enough that a stalled reader stops the client rather than growing without
// bound.
constexpr size_t PipeCapacity = 32 * 1024 * 1024;

// What GetSendBufSize reports. The Worker sizes its query batches from it and caps them at
// 8192 packets regardless.
constexpr int ReportedSendBufSize = 1024 * 1024;

struct Pipe
{
    std::vector<char> data;
    size_t head = 0;

    size_t Available() const { return data.size() - head; }

    void Compact()
    {
        if( head == data.size() )
        {
            data.clear();
            head = 0;
        }
        else if( head > PipeCapacity / 2 )
        {
            data.erase( data.begin(), data.begin() + head );
            head = 0;
        }
    }
};

}

struct InProcessConnection
{
    std::mutex lock;
    std::condition_variable changed;
    Pipe pipes[2];  // pipes[side] is what that side reads
    bool closed = false;
    std::atomic<int> refs { 2 };
};

namespace
{

struct Listener
{
    std::mutex lock;
    std::condition_variable pending;
    std::vector<InProcessConnection*> queue;
    bool listening = false;
};

Listener& GetListener()
{
    static Listener listener;
    return listener;
}

void CloseConnection( InProcessConnection* connection )
{
    {
        std::lock_guard<std::mutex> guard( connection->lock );
        connection->closed = true;
    }
    connection->changed.notify_all();
}

void ReleaseConnection( InProcessConnection* connection )
{
    if( connection->refs.fetch_sub( 1, std::memory_order_acq_rel ) == 1 )
    {
        delete connection;
    }
}

}

#ifdef _WIN32
void InitWinSock()
{
}
#endif

Socket::Socket()
    : m_live( nullptr )
    , m_owned( nullptr )
    , m_side( 0 )
{
}

Socket::Socket( InProcessConnection* connection, int side )
    : m_live( connection )
    , m_owned( connection )
    , m_side( side )
{
}

Socket::~Socket()
{
    Release();
}

void Socket::Release()
{
    if( m_owned )
    {
        CloseConnection( m_owned );
        ReleaseConnection( m_owned );
        m_owned = nullptr;
    }
    m_live.store( nullptr, std::memory_order_release );
}

bool Socket::Connect( const char*, uint16_t )
{
    assert( !IsValid() );
    Release();

    auto& listener = GetListener();
    std::lock_guard<std::mutex> guard( listener.lock );
    if( !listener.listening ) return false;

    auto connection = new InProcessConnection();
    listener.queue.push_back( connection );
    listener.pending.notify_all();

    m_owned = connection;
    m_side = 1;
    m_live.store( connection, std::memory_order_release );
    return true;
}

bool Socket::ConnectBlocking( const char* addr, uint16_t port )
{
    return Connect( addr, port );
}

void Socket::Close()
{
    auto connection = m_live.exchange( nullptr, std::memory_order_acq_rel );
    if( connection ) CloseConnection( connection );
}

int Socket::Send( const void* buf, int len )
{
    auto connection = m_live.load( std::memory_order_acquire );
    if( !connection ) return -1;
    if( len <= 0 ) return 0;

    auto src = (const char*)buf;
    auto& pipe = connection->pipes[1 - m_side];
    std::unique_lock<std::mutex> guard( connection->lock );
    int sent = 0;
    while( sent < len )
    {
        connection->changed.wait( guard, [&] { return connection->closed || pipe.Available() < PipeCapacity; } );
        if( connection->closed ) return -1;
        const auto room = PipeCapacity - pipe.Available();
        const auto chunk = std::min<size_t>( room, size_t( len - sent ) );
        pipe.data.insert( pipe.data.end(), src + sent, src + sent + chunk );
        sent += int( chunk );
        connection->changed.notify_all();
    }
    return sent;
}

int Socket::GetSendBufSize()
{
    return ReportedSendBufSize;
}

int Socket::Recv( void* buf, int len, int timeout )
{
    auto connection = m_live.load( std::memory_order_acquire );
    if( !connection ) return 0;
    if( len <= 0 ) return 0;

    auto& pipe = connection->pipes[m_side];
    std::unique_lock<std::mutex> guard( connection->lock );
    const auto ready = [&] { return connection->closed || pipe.Available() > 0; };
    if( timeout < 0 )
    {
        connection->changed.wait( guard, ready );
    }
    else if( !connection->changed.wait_for( guard, std::chrono::milliseconds( timeout ), ready ) )
    {
        return -1;
    }

    const auto available = pipe.Available();
    if( available == 0 ) return 0;  // closed and drained: end of stream

    const auto sz = std::min<size_t>( available, size_t( len ) );
    memcpy( buf, pipe.data.data() + pipe.head, sz );
    pipe.head += sz;
    pipe.Compact();
    connection->changed.notify_all();
    return int( sz );
}

int Socket::ReadUpTo( void* _buf, int len )
{
    auto buf = (char*)_buf;
    int rd = 0;
    while( len > 0 )
    {
        const auto res = Recv( buf, len, -1 );
        if( res == 0 ) break;
        if( res < 0 ) return -1;
        len -= res;
        rd += res;
        buf += res;
    }
    return rd;
}

bool Socket::Read( void* buf, int len, int timeout )
{
    auto cbuf = (char*)buf;
    while( len > 0 )
    {
        if( !ReadImpl( cbuf, len, timeout ) ) return false;
    }
    return true;
}

// A timeout is not a failure (the caller loops and re-checks its exit condition, as upstream
// does on POSIX); end of stream is.
bool Socket::ReadImpl( char*& buf, int& len, int timeout )
{
    const auto sz = Recv( buf, len, timeout );
    if( sz == 0 ) return false;
    if( sz > 0 )
    {
        len -= sz;
        buf += sz;
    }
    return true;
}

bool Socket::ReadRaw( void* _buf, int len, int timeout )
{
    auto buf = (char*)_buf;
    while( len > 0 )
    {
        const auto sz = Recv( buf, len, timeout );
        if( sz <= 0 ) return false;
        len -= sz;
        buf += sz;
    }
    return true;
}

bool Socket::HasData()
{
    auto connection = m_live.load( std::memory_order_acquire );
    if( !connection ) return false;
    std::lock_guard<std::mutex> guard( connection->lock );
    return connection->closed || connection->pipes[m_side].Available() > 0;
}

bool Socket::IsValid() const
{
    return m_live.load( std::memory_order_acquire ) != nullptr;
}


ListenSocket::ListenSocket()
    : m_listening( false )
{
}

ListenSocket::~ListenSocket()
{
    Close();
}

bool ListenSocket::Listen( uint16_t, int )
{
    auto& listener = GetListener();
    std::lock_guard<std::mutex> guard( listener.lock );
    if( listener.listening ) return false;
    listener.listening = true;
    m_listening = true;
    return true;
}

Socket* ListenSocket::Accept()
{
    auto& listener = GetListener();
    InProcessConnection* connection = nullptr;
    {
        std::unique_lock<std::mutex> guard( listener.lock );
        if( !listener.pending.wait_for( guard, std::chrono::milliseconds( 10 ), [&] { return !listener.queue.empty(); } ) )
        {
            return nullptr;
        }
        connection = listener.queue.front();
        listener.queue.erase( listener.queue.begin() );
    }

    auto ptr = (Socket*)tracy_malloc( sizeof( Socket ) );
    new(ptr) Socket( connection, 0 );
    return ptr;
}

void ListenSocket::Close()
{
    if( !m_listening ) return;
    m_listening = false;

    std::vector<InProcessConnection*> orphans;
    {
        auto& listener = GetListener();
        std::lock_guard<std::mutex> guard( listener.lock );
        listener.listening = false;
        orphans.swap( listener.queue );
    }
    // Each queued connection still holds the reference its accepted Socket would have taken.
    for( auto connection : orphans )
    {
        CloseConnection( connection );
        ReleaseConnection( connection );
    }
}


UdpBroadcast::UdpBroadcast()
{
}

UdpBroadcast::~UdpBroadcast()
{
}

bool UdpBroadcast::Open( const char*, uint16_t )
{
    return false;
}

void UdpBroadcast::Close()
{
}

int UdpBroadcast::Send( uint16_t, const void*, int )
{
    return -1;
}


IpAddress::IpAddress()
    : m_number( 0 )
{
    memcpy( m_text, "in-process", 11 );
}

IpAddress::~IpAddress()
{
}

void IpAddress::Set( const struct sockaddr& )
{
}


UdpListen::UdpListen()
{
}

UdpListen::~UdpListen()
{
}

bool UdpListen::Listen( uint16_t )
{
    return false;
}

void UdpListen::Close()
{
}

const char* UdpListen::Read( size_t&, IpAddress&, int )
{
    return nullptr;
}

}
