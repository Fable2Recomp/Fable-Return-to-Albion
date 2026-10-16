// The dev menu's view into the held capture: see the QUERIES note in refii_tracy_capture.h.
//
// Every call takes the capture lock (Discard cannot free the Worker underneath it) and then the
// Worker's main-thread lock, the same handover Tracy's own GUI uses, so a live capture's threads
// yield to the reader rather than starving it. The traversals follow the GUI's: timelines are
// time-ordered, found by binary search on zone end, and a timeline vector may hold its events
// inline ("magic") rather than as pointers.

#include "refii_tracy_capture.h"
#include "refii_tracy_internal.hpp"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <unordered_map>
#include <vector>

#include <capstone.h>

#include "server/TracyWorker.hpp"

namespace
{

using namespace tracy;

void WriteSlot( char* out, uint32_t stride, uint32_t index, const char* text )
{
    if( !out || stride == 0 ) return;
    char* slot = out + size_t( index ) * stride;
    size_t len = 0;
    if( text ) while( len + 1 < stride && text[len] != '\0' ) len++;
    memcpy( slot, text, len );
    slot[len] = '\0';
}

template<typename Body>
uint32_t WithWorker( Body&& body )
{
    std::lock_guard<std::mutex> guard( refii_tracy::CaptureLock() );
    Worker* worker = refii_tracy::HeldWorker();
    if( !worker || !worker->HasData() ) return 0;
    auto data = worker->ObtainLockForMainThread();
    return body( *worker );
}

struct ZoneSink
{
    int64_t t0, t1, minNs;
    uint32_t maxDepth, capacity, count;
    int64_t* start;
    int64_t* end;
    int16_t* srcloc;
    uint8_t* depth;

    bool Full() const { return count >= capacity; }

    void Add( int64_t s, int64_t e, int16_t loc, uint32_t d )
    {
        start[count] = s;
        end[count] = e;
        srcloc[count] = loc;
        depth[count] = uint8_t( std::min<uint32_t>( d, 255 ) );
        count++;
    }
};

void CollectCpuLevel( Worker& w, const Vector<short_ptr<ZoneEvent>>& vec, uint32_t depth, ZoneSink& sink );

template<typename Adapter, typename V>
void CollectCpuZones( Worker& w, const V& vec, uint32_t depth, ZoneSink& sink )
{
    auto it = std::lower_bound( vec.begin(), vec.end(), sink.t0, [&w]( const auto& l, int64_t r ) { Adapter a; return w.GetZoneEnd( a( l ) ) < r; } );
    for( ; it != vec.end() && !sink.Full(); ++it )
    {
        Adapter a;
        const ZoneEvent& ev = a( *it );
        const int64_t start = ev.Start();
        if( start > sink.t1 ) break;
        const int64_t end = w.GetZoneEnd( ev );
        if( end - start < sink.minNs ) continue;
        sink.Add( start, end, ev.SrcLoc(), depth );
        if( depth + 1 < sink.maxDepth && ev.HasChildren() )
        {
            CollectCpuLevel( w, w.GetZoneChildren( ev.Child() ), depth + 1, sink );
        }
    }
}

void CollectCpuLevel( Worker& w, const Vector<short_ptr<ZoneEvent>>& vec, uint32_t depth, ZoneSink& sink )
{
    if( vec.empty() ) return;
    if( vec.is_magic() )
    {
        CollectCpuZones<VectorAdapterDirect<ZoneEvent>>( w, *(const Vector<ZoneEvent>*)( &vec ), depth, sink );
    }
    else
    {
        CollectCpuZones<VectorAdapterPointer<ZoneEvent>>( w, vec, depth, sink );
    }
}

void CollectGpuLevel( Worker& w, const Vector<short_ptr<GpuEvent>>& vec, uint32_t depth, ZoneSink& sink );

// GPU events resolve late: an unresolved end is negative and sorts last as unsigned, as in the GUI.
template<typename Adapter, typename V>
void CollectGpuZones( Worker& w, const V& vec, uint32_t depth, ZoneSink& sink )
{
    auto it = std::lower_bound( vec.begin(), vec.end(), sink.t0, []( const auto& l, int64_t r ) { Adapter a; return uint64_t( a( l ).GpuEnd() ) < uint64_t( r ); } );
    for( ; it != vec.end() && !sink.Full(); ++it )
    {
        Adapter a;
        const GpuEvent& ev = a( *it );
        const int64_t start = ev.GpuStart();
        if( start < 0 ) continue;
        if( start > sink.t1 ) break;
        const int64_t end = w.GetZoneEnd( ev );
        if( end - start < sink.minNs ) continue;
        sink.Add( start, end, ev.SrcLoc(), depth );
        if( depth + 1 < sink.maxDepth && ev.Child() >= 0 )
        {
            CollectGpuLevel( w, w.GetGpuChildren( ev.Child() ), depth + 1, sink );
        }
    }
}

void CollectGpuLevel( Worker& w, const Vector<short_ptr<GpuEvent>>& vec, uint32_t depth, ZoneSink& sink )
{
    if( vec.empty() ) return;
    if( vec.is_magic() )
    {
        CollectGpuZones<VectorAdapterDirect<GpuEvent>>( w, *(const Vector<GpuEvent>*)( &vec ), depth, sink );
    }
    else
    {
        CollectGpuZones<VectorAdapterPointer<GpuEvent>>( w, vec, depth, sink );
    }
}

const char* PlotName( Worker& w, const PlotData& plot )
{
    switch( plot.type )
    {
    case PlotType::Memory: return plot.name == 0 ? "Memory usage" : w.GetString( plot.name );
    case PlotType::SysTime: return "CPU usage";
    default: return w.GetString( plot.name );
    }
}

int64_t EndOr( int64_t end, bool valid, int64_t fallback )
{
    return valid ? end : fallback;
}

}

extern "C" {

TRACY_API uint32_t RefiiTracyGetFrames( uint64_t first, uint32_t capacity, int64_t* out_start,
                                        int64_t* out_end, uint64_t* out_total )
{
    if( out_total ) *out_total = 0;
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const FrameData* base = w.GetFramesBase();
        if( !base ) return 0;
        const size_t all = w.GetFrameCount( *base );
        const size_t skip = w.IsOnDemand() ? std::min<size_t>( 2, all ) : 0;
        const uint64_t total = all - skip;
        if( out_total ) *out_total = total;
        uint32_t n = 0;
        for( uint64_t i = first; i < total && n < capacity; i++, n++ )
        {
            out_start[n] = w.GetFrameBegin( *base, size_t( skip + i ) );
            out_end[n] = w.GetFrameEnd( *base, size_t( skip + i ) );
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetThreads( uint32_t capacity, uint64_t* out_id, uint32_t* out_flags,
                                         char* out_names, uint32_t name_stride )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        for( const ThreadData* td : w.GetThreadData() )
        {
            if( n >= capacity ) break;
            const ContextSwitch* cs = w.GetContextSwitchData( td->id );
            uint32_t flags = 0;
            if( !td->timeline.empty() ) flags |= 1;
            if( !td->samples.empty() ) flags |= 2;
            if( cs && !cs->v.empty() ) flags |= 4;
            if( td->isFiber ) flags |= 8;
            out_id[n] = td->id;
            out_flags[n] = flags;
            WriteSlot( out_names, name_stride, n, w.GetThreadName( td->id ) );
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetZones( uint64_t thread, int64_t t0, int64_t t1, int64_t min_ns,
                                       uint32_t max_depth, uint32_t capacity, int64_t* out_start,
                                       int64_t* out_end, int16_t* out_srcloc, uint8_t* out_depth )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const ThreadData* td = w.GetThreadData( thread );
        if( !td ) return 0;
        ZoneSink sink { t0, t1, min_ns, max_depth, capacity, 0, out_start, out_end, out_srcloc, out_depth };
        CollectCpuLevel( w, td->timeline, 0, sink );
        return sink.count;
    } );
}

TRACY_API int RefiiTracyGetSourceLocation( int16_t srcloc, char* out_name, uint32_t name_size,
                                           char* out_function, uint32_t function_size,
                                           char* out_file, uint32_t file_size,
                                           uint32_t* out_line, uint32_t* out_color )
{
    return int( WithWorker( [&]( Worker& w ) -> uint32_t {
        const SourceLocation& loc = w.GetSourceLocation( srcloc );
        WriteSlot( out_name, name_size, 0, w.GetZoneName( loc ) );
        WriteSlot( out_function, function_size, 0, w.GetString( loc.function ) );
        WriteSlot( out_file, file_size, 0, w.GetString( loc.file ) );
        if( out_line ) *out_line = loc.line;
        if( out_color ) *out_color = loc.color;
        return 1;
    } ) );
}

TRACY_API uint32_t RefiiTracyGetZoneStats( uint32_t capacity, int16_t* out_srcloc,
                                           uint64_t* out_count, int64_t* out_total,
                                           int64_t* out_min, int64_t* out_max,
                                           int64_t* out_median, int64_t* out_p95,
                                           int64_t* out_self_total )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        std::vector<int64_t> durations;
        for( const auto& entry : w.GetSourceLocationZones() )
        {
            if( n >= capacity ) break;
            const auto& zones = entry.second;
            if( zones.zones.empty() ) continue;

            durations.clear();
            durations.reserve( zones.zones.size() );
            for( const auto& z : zones.zones )
            {
                const ZoneEvent* ev = z.Zone();
                durations.push_back( w.GetZoneEnd( *ev ) - ev->Start() );
            }
            const size_t mid = durations.size() / 2;
            std::nth_element( durations.begin(), durations.begin() + mid, durations.end() );
            const int64_t median = durations[mid];
            const size_t high = std::min( durations.size() - 1, durations.size() * 95 / 100 );
            std::nth_element( durations.begin(), durations.begin() + high, durations.end() );

            out_srcloc[n] = entry.first;
            out_count[n] = zones.zones.size();
            out_total[n] = zones.total;
            out_min[n] = zones.min;
            out_max[n] = zones.max;
            out_median[n] = median;
            out_p95[n] = durations[high];
            out_self_total[n] = zones.selfTotal;
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetPlots( uint32_t capacity, char* out_names, uint32_t name_stride,
                                       uint8_t* out_type, uint8_t* out_format,
                                       uint64_t* out_count, double* out_min, double* out_max )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        for( const PlotData* plot : w.GetPlots() )
        {
            if( n >= capacity ) break;
            WriteSlot( out_names, name_stride, n, PlotName( w, *plot ) );
            out_type[n] = uint8_t( plot->type );
            out_format[n] = uint8_t( plot->format );
            out_count[n] = plot->data.size();
            out_min[n] = plot->min;
            out_max[n] = plot->max;
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetPlotData( uint32_t plot, int64_t t0, int64_t t1, uint32_t capacity,
                                          int64_t* out_time, double* out_value )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const auto& plots = w.GetPlots();
        if( plot >= plots.size() || capacity == 0 ) return 0;
        auto& data = plots[plot]->data;
        data.ensure_sorted();  // as the GUI does before reading, under the same lock

        const auto lo = std::lower_bound( data.begin(), data.end(), t0, []( const PlotItem& l, int64_t r ) { return l.time.Val() < r; } );
        const auto hi = std::upper_bound( lo, data.end(), t1, []( int64_t l, const PlotItem& r ) { return l < r.time.Val(); } );
        const size_t count = size_t( hi - lo );
        uint32_t n = 0;
        if( count <= capacity )
        {
            for( auto it = lo; it != hi; ++it, ++n )
            {
                out_time[n] = it->time.Val();
                out_value[n] = it->val;
            }
            return n;
        }

        const size_t buckets = std::max<size_t>( 1, capacity / 2 );
        for( size_t b = 0; b < buckets; b++ )
        {
            auto first = lo + count * b / buckets;
            auto last = lo + count * ( b + 1 ) / buckets;
            if( first == last ) continue;
            auto minIt = first, maxIt = first;
            for( auto it = first; it != last; ++it )
            {
                if( it->val < minIt->val ) minIt = it;
                if( it->val > maxIt->val ) maxIt = it;
            }
            auto a = minIt, c = maxIt;
            if( c < a ) std::swap( a, c );
            out_time[n] = a->time.Val();
            out_value[n++] = a->val;
            if( c != a && n < capacity )
            {
                out_time[n] = c->time.Val();
                out_value[n++] = c->val;
            }
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetMessages( uint64_t first, uint32_t capacity, int64_t* out_time,
                                          uint64_t* out_thread, uint32_t* out_color,
                                          char* out_text, uint32_t text_stride,
                                          uint64_t* out_total )
{
    if( out_total ) *out_total = 0;
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const auto& messages = w.GetMessages();
        if( out_total ) *out_total = messages.size();
        uint32_t n = 0;
        for( uint64_t i = first; i < messages.size() && n < capacity; i++, n++ )
        {
            const MessageData& msg = *messages[size_t( i )];
            out_time[n] = msg.time;
            out_thread[n] = w.DecompressThread( msg.thread );
            out_color[n] = msg.color;
            WriteSlot( out_text, text_stride, n, w.GetString( msg.ref ) );
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetGpuContexts( uint32_t capacity, char* out_names,
                                             uint32_t name_stride, uint64_t* out_zones )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        for( const GpuCtxData* ctx : w.GetGpuData() )
        {
            if( n >= capacity ) break;
            WriteSlot( out_names, name_stride, n, ctx->name.Active() ? w.GetString( ctx->name ) : "GPU" );
            out_zones[n] = ctx->count;
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetGpuZones( uint32_t context, int64_t t0, int64_t t1, int64_t min_ns,
                                          uint32_t max_depth, uint32_t capacity,
                                          int64_t* out_start, int64_t* out_end,
                                          int16_t* out_srcloc, uint8_t* out_depth )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const auto& contexts = w.GetGpuData();
        if( context >= contexts.size() ) return 0;
        ZoneSink sink { t0, t1, min_ns, max_depth, capacity, 0, out_start, out_end, out_srcloc, out_depth };
        for( const auto& thread : contexts[context]->threadData )
        {
            CollectGpuLevel( w, thread.second.timeline, 0, sink );
        }
        return sink.count;
    } );
}

TRACY_API uint32_t RefiiTracyGetRunning( uint64_t thread, int64_t t0, int64_t t1, uint32_t capacity,
                                         int64_t* out_start, int64_t* out_end, uint8_t* out_cpu )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const ContextSwitch* cs = w.GetContextSwitchData( thread );
        if( !cs ) return 0;
        const int64_t last = w.GetLastTime();
        const auto& v = cs->v;
        auto it = std::lower_bound( v.begin(), v.end(), t0, [last]( const ContextSwitchData& l, int64_t r ) { return EndOr( l.End(), l.IsEndValid(), last ) < r; } );
        uint32_t n = 0;
        for( ; it != v.end() && n < capacity; ++it )
        {
            if( it->Start() > t1 ) break;
            out_start[n] = it->Start();
            out_end[n] = EndOr( it->End(), it->IsEndValid(), last );
            out_cpu[n] = it->Cpu();
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetCpuCount( void )
{
    return WithWorker( []( Worker& w ) -> uint32_t { return uint32_t( w.GetCpuDataCpuCount() ); } );
}

TRACY_API uint32_t RefiiTracyGetCpuSlices( uint32_t cpu, int64_t t0, int64_t t1, uint32_t capacity,
                                           int64_t* out_start, int64_t* out_end,
                                           uint64_t* out_thread )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        if( int( cpu ) >= w.GetCpuDataCpuCount() ) return 0;
        const int64_t last = w.GetLastTime();
        const auto& cs = w.GetCpuData()[cpu].cs;
        auto it = std::lower_bound( cs.begin(), cs.end(), t0, [last]( const ContextSwitchCpu& l, int64_t r ) { return EndOr( l.End(), l.IsEndValid(), last ) < r; } );
        uint32_t n = 0;
        for( ; it != cs.end() && n < capacity; ++it )
        {
            if( it->Start() > t1 ) break;
            out_start[n] = it->Start();
            out_end[n] = EndOr( it->End(), it->IsEndValid(), last );
            out_thread[n] = w.DecompressThreadExternal( it->Thread() );
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetSampledSymbols( uint32_t capacity, uint64_t* out_addr,
                                                uint32_t* out_incl, uint32_t* out_excl,
                                                char* out_names, uint32_t name_stride,
                                                char* out_files, uint32_t file_stride,
                                                uint32_t* out_line )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        std::vector<std::pair<uint64_t, const SymbolStats*>> symbols;
        for( const auto& entry : w.GetSymbolStats() )
        {
            if( entry.second.excl > 0 || entry.second.incl > 0 ) symbols.emplace_back( entry.first, &entry.second );
        }
        std::sort( symbols.begin(), symbols.end(), []( const auto& l, const auto& r ) {
            return l.second->excl != r.second->excl ? l.second->excl > r.second->excl : l.second->incl > r.second->incl;
        } );

        uint32_t n = 0;
        for( const auto& symbol : symbols )
        {
            if( n >= capacity ) break;
            out_addr[n] = symbol.first;
            // Tracy counts a sample as exclusive on the leaf and inclusive on every frame above
            // it, so a function's inclusive total is both.
            out_incl[n] = symbol.second->incl + symbol.second->excl;
            out_excl[n] = symbol.second->excl;
            if( const SymbolData* data = w.GetSymbolData( symbol.first ) )
            {
                WriteSlot( out_names, name_stride, n, w.GetString( data->name ) );
                WriteSlot( out_files, file_stride, n, w.GetString( data->file ) );
                out_line[n] = data->line;
            }
            else
            {
                char address[32];
                snprintf( address, sizeof( address ), "0x%llx", (unsigned long long)symbol.first );
                WriteSlot( out_names, name_stride, n, address );
                WriteSlot( out_files, file_stride, n, "" );
                out_line[n] = 0;
            }
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyDisassemble( uint64_t symbol, uint32_t capacity, uint64_t* out_addr,
                                          uint32_t* out_samples, char* out_text,
                                          uint32_t text_stride )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t len = 0;
        const char* code = w.GetSymbolCode( symbol, len );
        if( !code || len == 0 ) return 0;

        cs_arch arch;
        cs_mode mode;
        switch( w.GetCpuArch() )
        {
        case CpuArchX64: arch = CS_ARCH_X86; mode = CS_MODE_64; break;
        case CpuArchX86: arch = CS_ARCH_X86; mode = CS_MODE_32; break;
        case CpuArchArm64: arch = CS_ARCH_AARCH64; mode = CS_MODE_ARM; break;
        default: return 0;
        }
        csh handle;
        if( cs_open( arch, mode, &handle ) != CS_ERR_OK ) return 0;

        std::unordered_map<uint64_t, uint32_t> hits;
        if( const auto ips = w.GetSymbolInstructionPointers( symbol ) )
        {
            for( const auto& ip : *ips ) hits[w.GetCanonicalPointer( ip.first )] += ip.second;
        }

        cs_insn* insn = nullptr;
        const size_t count = cs_disasm( handle, (const uint8_t*)code, len, symbol, 0, &insn );
        uint32_t n = 0;
        for( size_t i = 0; i < count && n < capacity; i++, n++ )
        {
            out_addr[n] = insn[i].address;
            const auto hit = hits.find( insn[i].address );
            out_samples[n] = hit == hits.end() ? 0 : hit->second;
            char line[256];
            snprintf( line, sizeof( line ), "%s %s", insn[i].mnemonic, insn[i].op_str );
            WriteSlot( out_text, text_stride, n, line );
        }
        if( insn ) cs_free( insn, count );
        cs_close( &handle );
        return n;
    } );
}

}
