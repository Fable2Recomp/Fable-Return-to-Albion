#ifndef __TRACYSOCKET_HPP__
#define __TRACYSOCKET_HPP__

// REFII: IN-PROCESS TRANSPORT. Tracy 0.13.1 is pinned and the profiler is the game's own: the
// client and the analysis Worker live in the same library and talk through an in-memory duplex
// pipe with the semantics of the TCP stream they were written against. There is no socket, no
// listen port and no broadcast; the public interface below is upstream's, so neither side of
// Tracy needed changing.

#include <atomic>
#include <stddef.h>
#include <stdint.h>

struct sockaddr;

namespace tracy
{

struct InProcessConnection;

#ifdef _WIN32
void InitWinSock();
#endif

class Socket
{
public:
    Socket();
    Socket( InProcessConnection* connection, int side );
    ~Socket();

    bool Connect( const char* addr, uint16_t port );
    bool ConnectBlocking( const char* addr, uint16_t port );
    void Close();

    int Send( const void* buf, int len );
    int GetSendBufSize();

    int ReadUpTo( void* buf, int len );
    bool Read( void* buf, int len, int timeout );

    template<typename ShouldExit>
    bool Read( void* buf, int len, int timeout, ShouldExit exitCb )
    {
        auto cbuf = (char*)buf;
        while( len > 0 )
        {
            if( exitCb() ) return false;
            if( !ReadImpl( cbuf, len, timeout ) ) return false;
        }
        return true;
    }

    bool ReadRaw( void* buf, int len, int timeout );
    bool HasData();
    bool IsValid() const;

    Socket( const Socket& ) = delete;
    Socket( Socket&& ) = delete;
    Socket& operator=( const Socket& ) = delete;
    Socket& operator=( Socket&& ) = delete;

private:
    int Recv( void* buf, int len, int timeout );
    bool ReadImpl( char*& buf, int& len, int timeout );
    void Release();

    // m_live is what the I/O paths read and Close clears; m_owned keeps the connection alive
    // until the destructor, because the Worker's query path can still be inside Send on one
    // thread while its network thread closes the socket on another.
    std::atomic<InProcessConnection*> m_live;
    InProcessConnection* m_owned;
    int m_side;
};

class ListenSocket
{
public:
    ListenSocket();
    ~ListenSocket();

    bool Listen( uint16_t port, int backlog );
    Socket* Accept();
    void Close();

    ListenSocket( const ListenSocket& ) = delete;
    ListenSocket( ListenSocket&& ) = delete;
    ListenSocket& operator=( const ListenSocket& ) = delete;
    ListenSocket& operator=( ListenSocket&& ) = delete;

private:
    bool m_listening;
};

class UdpBroadcast
{
public:
    UdpBroadcast();
    ~UdpBroadcast();

    bool Open( const char* addr, uint16_t port );
    void Close();

    int Send( uint16_t port, const void* data, int len );

    UdpBroadcast( const UdpBroadcast& ) = delete;
    UdpBroadcast( UdpBroadcast&& ) = delete;
    UdpBroadcast& operator=( const UdpBroadcast& ) = delete;
    UdpBroadcast& operator=( UdpBroadcast&& ) = delete;
};

class IpAddress
{
public:
    IpAddress();
    ~IpAddress();

    void Set( const struct sockaddr& addr );

    uint32_t GetNumber() const { return m_number; }
    const char* GetText() const { return m_text; }

    IpAddress( const IpAddress& ) = delete;
    IpAddress( IpAddress&& ) = delete;
    IpAddress& operator=( const IpAddress& ) = delete;
    IpAddress& operator=( IpAddress&& ) = delete;

private:
    uint32_t m_number;
    char m_text[17];
};

class UdpListen
{
public:
    UdpListen();
    ~UdpListen();

    bool Listen( uint16_t port );
    void Close();

    const char* Read( size_t& len, IpAddress& addr, int timeout );

    UdpListen( const UdpListen& ) = delete;
    UdpListen( UdpListen&& ) = delete;
    UdpListen& operator=( const UdpListen& ) = delete;
    UdpListen& operator=( UdpListen&& ) = delete;
};

}

#endif
