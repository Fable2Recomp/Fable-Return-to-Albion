#include "refii_tracy_capture.h"
#include "refii_tracy_internal.hpp"

#include <atomic>
#include <chrono>
#include <ctime>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

#ifdef _WIN32
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <semaphore.h>
#  include <time.h>
#endif

#include "server/TracyFileWrite.hpp"
#include "server/TracyMemory.hpp"
#include "server/TracyWorker.hpp"

namespace tracy
{
// Declared here rather than through client/TracyProfiler.hpp, whose types share the tracy
// namespace with the Worker's.
TRACY_API bool IsProfilerStarted();
TRACY_API bool BeginSamplingProfiling();
TRACY_API void EndSamplingProfiling();
}

namespace
{

// Connect ignores both; there is one client in the process.
constexpr const char* CaptureAddress = "in-process";
constexpr uint16_t CapturePort = 8086;

// How long Save waits for a stopped stream to finish closing before writing.
constexpr auto StreamCloseWait = std::chrono::seconds( 5 );

std::mutex s_captureLock;
std::unique_ptr<tracy::Worker> s_worker;
int64_t s_memoryLimit = 0;
bool s_stopped = false;
bool s_systemTracing = false;

// Sampling and context switches (perf/ftrace, ETW) run only while a capture streams: the
// library is built with TRACY_SAMPLING_PROFILER_MANUAL_START, so an idle game traces nothing.
// Tracy asserts on a second Begin, hence the flag. Called under s_captureLock.
bool s_sampling = false;

void EndSampling()
{
    if( !s_sampling ) return;
    tracy::EndSamplingProfiling();
    s_sampling = false;
}

// ---- crash save ----------------------------------------------------------------------------
//
// A crash handler may not allocate, lock or start threads, and saving a capture does all three.
// So the work belongs to a thread started when the crash directory is set, parked on a
// semaphore; the handler flips an atomic, posts, and waits a bounded time.

std::string s_crashDirectory;
std::atomic<bool> s_saverReady { false };
std::atomic<bool> s_live { false };       // a capture is held and has not been stopped
std::atomic<int> s_crashState { 0 };      // 0 idle, 1 requested, 2 saved, 3 failed

#ifdef _WIN32
HANDLE s_crashEvent = nullptr;
#else
sem_t s_crashSemaphore;
#endif

std::string CrashCapturePath()
{
    const std::time_t now = std::time( nullptr );
    std::tm local {};
#ifdef _WIN32
    localtime_s( &local, &now );
#else
    localtime_r( &now, &local );
#endif
    char name[64];
    std::strftime( name, sizeof( name ), "crash-%Y%m%d-%H%M%S.tracy", &local );
    return s_crashDirectory + "/" + name;
}

void CrashSaver()
{
    for(;;)
    {
#ifdef _WIN32
        WaitForSingleObject( s_crashEvent, INFINITE );
#else
        while( sem_wait( &s_crashSemaphore ) != 0 ) {}
#endif
        if( s_crashState.load() != 1 ) continue;
        RefiiTracyCaptureStop();
        const int saved = RefiiTracyCaptureSave( CrashCapturePath().c_str() );
        s_crashState.store( saved ? 2 : 3 );
    }
}

void SleepBriefly()
{
#ifdef _WIN32
    Sleep( 10 );
#else
    timespec wait { 0, 10 * 1000 * 1000 };
    nanosleep( &wait, nullptr );
#endif
}

}

namespace refii_tracy
{

std::mutex& CaptureLock()
{
    return s_captureLock;
}

tracy::Worker* HeldWorker()
{
    return s_worker.get();
}

}

extern "C" {

TRACY_API int RefiiTracyCaptureStart( int64_t memory_limit_bytes, int system_tracing )
{
    std::lock_guard<std::mutex> guard( s_captureLock );
    if( !tracy::IsProfilerStarted() ) return 0;
    if( s_worker && !s_stopped && s_worker->IsConnected() ) return 0;

    EndSampling();
    s_worker.reset();
    s_memoryLimit = memory_limit_bytes;
    s_stopped = false;
    s_systemTracing = system_tracing != 0;
    s_worker = std::make_unique<tracy::Worker>( CaptureAddress, CapturePort, memory_limit_bytes > 0 ? memory_limit_bytes : -1 );
    s_sampling = s_systemTracing && tracy::BeginSamplingProfiling();
    s_live.store( true );
    return 1;
}

TRACY_API void RefiiTracyCaptureStop( void )
{
    std::lock_guard<std::mutex> guard( s_captureLock );
    s_live.store( false );
    if( !s_worker || s_stopped ) return;
    EndSampling();
    s_worker->Disconnect();
    s_stopped = true;
}

TRACY_API void RefiiTracyCaptureDiscard( void )
{
    std::unique_ptr<tracy::Worker> worker;
    {
        std::lock_guard<std::mutex> guard( s_captureLock );
        s_live.store( false );
        EndSampling();
        worker = std::move( s_worker );
        s_stopped = false;
        s_memoryLimit = 0;
    }
    // Joins the Worker's threads; outside the lock so a status read never waits on it.
    worker.reset();
}

TRACY_API void RefiiTracyCaptureGetStatus( RefiiTracyCaptureStatus* out )
{
    if( !out ) return;
    *out = RefiiTracyCaptureStatus {};

    std::lock_guard<std::mutex> guard( s_captureLock );
    if( !s_worker ) return;

    auto& worker = *s_worker;
    const bool connected = worker.IsConnected();
    if( s_stopped || ( !connected && worker.HasData() ) )
    {
        // The stream can also end on its own (memory limit, failure); sampling ends with it.
        EndSampling();
        out->state = REFII_TRACY_CAPTURE_STOPPED;
    }
    else
    {
        out->state = connected && worker.HasData() ? REFII_TRACY_CAPTURE_LIVE : REFII_TRACY_CAPTURE_CONNECTING;
    }
    out->failure = int32_t( worker.GetFailureType() );
    out->memory_bytes = tracy::memUsage.load( std::memory_order_relaxed );
    out->memory_limit = s_memoryLimit;
    out->system_tracing = s_systemTracing ? 1 : 0;

    if( !worker.HasData() ) return;

    auto data = worker.ObtainLockForMainThread();
    out->zones = worker.GetZoneCount();
    out->samples = worker.GetCallstackSampleCount();
    out->context_switches = worker.HasContextSwitches() ? 1 : 0;
    out->last_ns = worker.GetLastTime();
    if( const auto base = worker.GetFramesBase() )
    {
        out->frames = worker.GetFrameCount( *base );
        // An on-demand capture's timeline starts at its third frame (the first two predate
        // the connection); GetFirstTime knows that and asserts below three.
        if( base->frames.size() >= 3 ) out->first_ns = worker.GetFirstTime();
    }
}

TRACY_API int RefiiTracyCaptureIsLive( void )
{
    return s_live.load( std::memory_order_relaxed ) ? 1 : 0;
}

TRACY_API int RefiiTracyCaptureSave( const char* path )
{
    if( !path ) return 0;

    std::lock_guard<std::mutex> guard( s_captureLock );
    if( !s_worker || !s_stopped || !s_worker->HasData() ) return 0;

    // Stop asks the stream to end; the Worker's threads finish it. Write reads what they wrote.
    const auto deadline = std::chrono::steady_clock::now() + StreamCloseWait;
    while( s_worker->IsConnected() && std::chrono::steady_clock::now() < deadline )
    {
        std::this_thread::sleep_for( std::chrono::milliseconds( 10 ) );
    }

    std::unique_ptr<tracy::FileWrite> file( tracy::FileWrite::Open( path, tracy::FileCompression::Zstd, 3 ) );
    if( !file ) return 0;
    s_worker->Write( *file, false );
    file->Finish();
    return 1;
}

TRACY_API void RefiiTracyCaptureSetCrashDirectory( const char* directory )
{
    if( !directory || s_saverReady.load() ) return;
    s_crashDirectory = directory;
#ifdef _WIN32
    s_crashEvent = CreateEventA( nullptr, FALSE, FALSE, nullptr );
    if( !s_crashEvent ) return;
#else
    if( sem_init( &s_crashSemaphore, 0, 0 ) != 0 ) return;
#endif
    std::thread( CrashSaver ).detach();
    s_saverReady.store( true );
}

TRACY_API int RefiiTracyCaptureSaveOnCrash( uint32_t timeout_ms )
{
    if( !s_saverReady.load() || !s_live.load() ) return 0;

    int idle = 0;
    if( s_crashState.compare_exchange_strong( idle, 1 ) )
    {
#ifdef _WIN32
        SetEvent( s_crashEvent );
#else
        sem_post( &s_crashSemaphore );
#endif
    }

    for( uint32_t waited = 0; waited < timeout_ms && s_crashState.load() == 1; waited += 10 )
    {
        SleepBriefly();
    }
    return s_crashState.load() == 2 ? 1 : 0;
}

}
