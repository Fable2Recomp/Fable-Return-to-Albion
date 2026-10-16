#include "TracySysTime.hpp"

#ifdef TRACY_HAS_SYSTIME

// REFII: GAME ONLY. Upstream plots the whole machine's CPU load (GetSystemTimes, /proc/stat); this
// copy plots the game's own share of all cores, the way Tracy's Xbox (GDK) build reads
// GetProcessTimes. The header's fields are reused: `used` is this process's CPU time and `idle`
// the wall clock, both in nanoseconds; Get is CPU time over wall time times the core count.

#  if defined _WIN32
#    include <windows.h>
#  else
#    include <time.h>
#  endif

#include <algorithm>
#include <thread>

namespace tracy
{

#  if defined _WIN32

static inline uint64_t ConvertTime( const FILETIME& t )
{
    return ( uint64_t( t.dwHighDateTime ) << 32 ) | uint64_t( t.dwLowDateTime );
}

void SysTime::ReadTimes()
{
    FILETIME creationTime, exitTime, kernelTime, userTime, now;
    GetProcessTimes( GetCurrentProcess(), &creationTime, &exitTime, &kernelTime, &userTime );
    GetSystemTimeAsFileTime( &now );
    used = ( ConvertTime( kernelTime ) + ConvertTime( userTime ) ) * 100;  // 100 ns units
    idle = ConvertTime( now ) * 100;
}

#  else

void SysTime::ReadTimes()
{
    timespec cpu, wall;
    clock_gettime( CLOCK_PROCESS_CPUTIME_ID, &cpu );
    clock_gettime( CLOCK_MONOTONIC, &wall );
    used = uint64_t( cpu.tv_sec ) * 1000000000ull + uint64_t( cpu.tv_nsec );
    idle = uint64_t( wall.tv_sec ) * 1000000000ull + uint64_t( wall.tv_nsec );
}

#  endif

SysTime::SysTime()
{
    ReadTimes();
}

float SysTime::Get()
{
    const auto oldCpu = used;
    const auto oldWall = idle;

    ReadTimes();

    const auto cpu = used - oldCpu;
    const auto wall = idle - oldWall;
    const auto cores = std::max( 1u, std::thread::hardware_concurrency() );
    return wall == 0 ? -1 : cpu * 100.f / ( float( wall ) * cores );
}

}

#endif
