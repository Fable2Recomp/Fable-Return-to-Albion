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
#include <cmath>
#include <limits>
#include <unordered_map>
#include <utility>
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

// An instance's self time: its span minus its direct children's.
int64_t SelfTime( Worker& w, const ZoneEvent& ev )
{
    int64_t self = w.GetZoneEnd( ev ) - ev.Start();
    if( !ev.HasChildren() ) return self;
    const auto& children = w.GetZoneChildren( ev.Child() );
    if( children.is_magic() )
    {
        for( const auto& child : *(const Vector<ZoneEvent>*)( &children ) ) self -= w.GetZoneEnd( child ) - child.Start();
    }
    else
    {
        for( const auto& child : children ) self -= w.GetZoneEnd( *child ) - child->Start();
    }
    return self;
}

// Every instance of a location starting in [t0, t1], on `thread` or any (0). The list is sorted
// first, as the GUI does before searching it, under the same lock.
template<typename Zones, typename Visit>
void ForInstances( Worker& w, Zones& zones, uint64_t thread, int64_t t0, int64_t t1, Visit&& visit )
{
    auto& list = zones.zones;
    list.ensure_sorted();
    auto it = std::lower_bound( list.begin(), list.end(), t0, []( const auto& l, int64_t r ) { return l.Zone()->Start() < r; } );
    for( ; it != list.end(); ++it )
    {
        const ZoneEvent* ev = it->Zone();
        if( ev->Start() > t1 ) break;
        const uint64_t tid = w.DecompressThread( it->Thread() );
        if( thread != 0 && tid != thread ) continue;
        visit( *ev, tid );
    }
}

// Median and 95th percentile of `durations` (reordered).
std::pair<int64_t, int64_t> Percentiles( std::vector<int64_t>& durations )
{
    const size_t mid = durations.size() / 2;
    std::nth_element( durations.begin(), durations.begin() + mid, durations.end() );
    const int64_t median = durations[mid];
    const size_t high = std::min( durations.size() - 1, durations.size() * 95 / 100 );
    std::nth_element( durations.begin(), durations.begin() + high, durations.end() );
    return { median, durations[high] };
}

uint32_t ZoneStats( Worker& w, bool whole, uint64_t thread, int64_t t0, int64_t t1, uint32_t capacity,
                    int16_t* out_srcloc, uint64_t* out_count, int64_t* out_total, int64_t* out_min,
                    int64_t* out_max, int64_t* out_median, int64_t* out_p95, int64_t* out_self_total )
{
    std::vector<int16_t> locations;
    for( const auto& entry : w.GetSourceLocationZones() )
    {
        if( !entry.second.zones.empty() ) locations.push_back( entry.first );
    }

    uint32_t n = 0;
    std::vector<int64_t> durations;
    for( const int16_t srcloc : locations )
    {
        if( n >= capacity ) break;
        auto& zones = w.GetZonesForSourceLocation( srcloc );
        durations.clear();
        int64_t total = 0, self = 0;
        int64_t lo = std::numeric_limits<int64_t>::max(), hi = std::numeric_limits<int64_t>::min();
        if( whole )
        {
            // The Worker keeps these per location as the capture streams.
            durations.reserve( zones.zones.size() );
            for( const auto& z : zones.zones ) durations.push_back( w.GetZoneEnd( *z.Zone() ) - z.Zone()->Start() );
            total = zones.total;
            lo = zones.min;
            hi = zones.max;
            self = zones.selfTotal;
        }
        else
        {
            ForInstances( w, zones, thread, t0, t1, [&]( const ZoneEvent& ev, uint64_t ) {
                const int64_t d = w.GetZoneEnd( ev ) - ev.Start();
                durations.push_back( d );
                total += d;
                lo = std::min( lo, d );
                hi = std::max( hi, d );
                self += SelfTime( w, ev );
            } );
        }
        if( durations.empty() ) continue;

        const auto [median, p95] = Percentiles( durations );
        out_srcloc[n] = srcloc;
        out_count[n] = durations.size();
        out_total[n] = total;
        out_min[n] = lo;
        out_max[n] = hi;
        out_median[n] = median;
        out_p95[n] = p95;
        out_self_total[n] = self;
        n++;
    }
    return n;
}

struct TreeNode
{
    int32_t parent;
    uint64_t addr;
    StringIdx name;
    uint32_t total = 0;
    uint32_t self = 0;
    std::vector<uint32_t> children;
};

struct TreeKey
{
    int32_t parent;
    uint64_t function;
    bool operator==( const TreeKey& o ) const { return parent == o.parent && function == o.function; }
};

struct TreeKeyHash
{
    size_t operator()( const TreeKey& k ) const { return std::hash<uint64_t>()( k.function * 0x9E3779B97F4A7C15ull ^ uint64_t( uint32_t( k.parent ) ) ); }
};

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
        return ZoneStats( w, true, 0, 0, 0, capacity, out_srcloc, out_count, out_total, out_min, out_max, out_median, out_p95, out_self_total );
    } );
}

TRACY_API uint32_t RefiiTracyGetZoneStatsRange( uint64_t thread, int64_t t0, int64_t t1,
                                                uint32_t capacity, int16_t* out_srcloc,
                                                uint64_t* out_count, int64_t* out_total,
                                                int64_t* out_min, int64_t* out_max,
                                                int64_t* out_median, int64_t* out_p95,
                                                int64_t* out_self_total )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        return ZoneStats( w, false, thread, t0, t1, capacity, out_srcloc, out_count, out_total, out_min, out_max, out_median, out_p95, out_self_total );
    } );
}

TRACY_API uint32_t RefiiTracyFindZone( int16_t srcloc, uint64_t thread, int64_t t0, int64_t t1,
                                       uint32_t buckets, int log_scale, uint64_t* out_histogram,
                                       int64_t* out_min, int64_t* out_max, uint64_t* out_count,
                                       int64_t* out_total, int64_t* out_median, int64_t* out_p95,
                                       uint32_t slowest_capacity, int64_t* out_slow_start,
                                       int64_t* out_slow_end, uint64_t* out_slow_thread )
{
    for( uint32_t b = 0; b < buckets; b++ ) out_histogram[b] = 0;
    *out_min = *out_max = *out_total = *out_median = *out_p95 = 0;
    *out_count = 0;
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        struct Instance { int64_t start, end; uint64_t thread; };
        std::vector<Instance> instances;
        ForInstances( w, w.GetZonesForSourceLocation( srcloc ), thread, t0, t1, [&]( const ZoneEvent& ev, uint64_t tid ) {
            instances.push_back( Instance { ev.Start(), w.GetZoneEnd( ev ), tid } );
        } );
        if( instances.empty() ) return 0;

        std::vector<int64_t> durations;
        durations.reserve( instances.size() );
        int64_t total = 0;
        for( const auto& i : instances )
        {
            durations.push_back( i.end - i.start );
            total += i.end - i.start;
        }
        const int64_t lo = *std::min_element( durations.begin(), durations.end() );
        const int64_t hi = *std::max_element( durations.begin(), durations.end() );
        *out_min = lo;
        *out_max = hi;
        *out_count = instances.size();
        *out_total = total;

        if( buckets > 0 )
        {
            const bool logs = log_scale != 0 && lo > 0;
            const double a = logs ? std::log( double( lo ) ) : double( lo );
            const double span = ( logs ? std::log( double( hi ) ) : double( hi ) ) - a;
            for( const int64_t d : durations )
            {
                const double x = ( logs ? std::log( double( std::max<int64_t>( d, 1 ) ) ) : double( d ) ) - a;
                const uint32_t bucket = span > 0 ? std::min<uint32_t>( buckets - 1, uint32_t( x / span * buckets ) ) : 0;
                out_histogram[bucket]++;
            }
        }

        const auto [median, p95] = Percentiles( durations );
        *out_median = median;
        *out_p95 = p95;

        const size_t keep = std::min<size_t>( slowest_capacity, instances.size() );
        std::partial_sort( instances.begin(), instances.begin() + keep, instances.end(), []( const Instance& l, const Instance& r ) {
            return l.end - l.start > r.end - r.start;
        } );
        for( size_t i = 0; i < keep; i++ )
        {
            out_slow_start[i] = instances[i].start;
            out_slow_end[i] = instances[i].end;
            out_slow_thread[i] = instances[i].thread;
        }
        return uint32_t( keep );
    } );
}

TRACY_API uint32_t RefiiTracyGetSampleTree( uint64_t thread, int64_t t0, int64_t t1, int inverted,
                                            uint32_t min_samples, uint32_t capacity,
                                            int32_t* out_parent, uint8_t* out_depth,
                                            uint64_t* out_addr, uint32_t* out_total,
                                            uint32_t* out_self, char* out_names,
                                            uint32_t name_stride, uint64_t* out_samples )
{
    if( out_samples ) *out_samples = 0;
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        std::vector<TreeNode> nodes;
        std::vector<uint32_t> roots;
        std::unordered_map<TreeKey, uint32_t, TreeKeyHash> index;
        std::vector<const CallstackFrame*> path;
        uint64_t samples = 0;

        for( const ThreadData* td : w.GetThreadData() )
        {
            if( thread != 0 && td->id != thread ) continue;
            for( const SampleData& sample : td->samples )
            {
                const int64_t time = sample.time.Val();
                if( time < t0 || time > t1 ) continue;

                // The call stack lists the leaf first; each frame lists its inline frames
                // innermost first. Top-down walks both outermost first.
                const auto& stack = w.GetCallstack( sample.callstack.Val() );
                path.clear();
                for( uint16_t i = 0; i < stack.size(); i++ )
                {
                    const CallstackFrameData* frames = w.GetCallstackFrame( stack[i] );
                    if( !frames ) continue;
                    for( uint8_t f = 0; f < frames->size; f++ ) path.push_back( &frames->data[f] );
                }
                if( path.empty() ) continue;
                if( !inverted ) std::reverse( path.begin(), path.end() );
                samples++;

                int32_t parent = -1;
                for( const CallstackFrame* frame : path )
                {
                    const uint64_t function = frame->symAddr != 0 ? frame->symAddr : ( 1ull << 63 ) | frame->name.Idx();
                    auto [it, inserted] = index.try_emplace( TreeKey { parent, function }, uint32_t( nodes.size() ) );
                    if( inserted )
                    {
                        nodes.push_back( TreeNode { parent, frame->symAddr, frame->name } );
                        if( parent < 0 ) roots.push_back( it->second ); else nodes[parent].children.push_back( it->second );
                    }
                    nodes[it->second].total++;
                    parent = int32_t( it->second );
                }
                nodes[parent].self++;
            }
        }
        if( out_samples ) *out_samples = samples;

        // Depth first, heaviest child first, light branches pruned.
        uint32_t n = 0;
        auto byTotal = [&nodes]( uint32_t l, uint32_t r ) { return nodes[l].total > nodes[r].total; };
        std::sort( roots.begin(), roots.end(), byTotal );
        std::vector<std::pair<uint32_t, int32_t>> stack;  // node, output index of its parent
        for( auto it = roots.rbegin(); it != roots.rend(); ++it ) stack.emplace_back( *it, -1 );
        while( !stack.empty() && n < capacity )
        {
            const auto [node, parentOut] = stack.back();
            stack.pop_back();
            TreeNode& data = nodes[node];
            if( data.total < min_samples ) continue;
            const uint8_t depth = parentOut < 0 ? 0 : uint8_t( std::min( 255, out_depth[parentOut] + 1 ) );
            out_parent[n] = parentOut;
            out_depth[n] = depth;
            out_addr[n] = data.addr;
            out_total[n] = data.total;
            out_self[n] = data.self;
            WriteSlot( out_names, name_stride, n, w.GetString( data.name ) );
            std::sort( data.children.begin(), data.children.end(), byTotal );
            for( auto child = data.children.rbegin(); child != data.children.rend(); ++child ) stack.emplace_back( *child, int32_t( n ) );
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetLocks( int64_t t0, int64_t t1, uint32_t capacity, char* out_names,
                                       uint32_t name_stride, uint32_t* out_threads,
                                       uint64_t* out_acquired, uint64_t* out_contended,
                                       int64_t* out_wait_total, int64_t* out_wait_max,
                                       int64_t* out_hold_total, int64_t* out_hold_max,
                                       int64_t* out_worst_wait_at )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        for( const auto& entry : w.GetLockMap() )
        {
            if( n >= capacity ) break;
            const LockMap& lock = *entry.second;
            if( !lock.valid ) continue;

            // Per thread (the lock's own small index): when it began waiting, whether someone
            // else held the lock then, and when it obtained it.
            int64_t waitAt[64], obtainAt[64];
            bool contended[64];
            for( int i = 0; i < 64; i++ ) { waitAt[i] = -1; obtainAt[i] = -1; contended[i] = false; }

            uint64_t acquired = 0, contendedCount = 0;
            int64_t waitTotal = 0, waitMax = 0, holdTotal = 0, holdMax = 0, worstAt = 0;
            for( const auto& ptr : lock.timeline )
            {
                const LockEvent& ev = *ptr.ptr;
                const uint8_t t = ev.thread & 63;
                const int64_t time = ev.Time();
                switch( ev.type )
                {
                case LockEvent::Type::Wait:
                case LockEvent::Type::WaitShared:
                    waitAt[t] = time;
                    contended[t] = ptr.lockCount > 0;
                    break;
                case LockEvent::Type::Obtain:
                case LockEvent::Type::ObtainShared:
                    obtainAt[t] = time;
                    if( time >= t0 && time <= t1 )
                    {
                        acquired++;
                        if( waitAt[t] >= 0 )
                        {
                            const int64_t wait = time - waitAt[t];
                            waitTotal += wait;
                            if( wait > waitMax ) { waitMax = wait; worstAt = waitAt[t]; }
                            if( contended[t] ) contendedCount++;
                        }
                    }
                    waitAt[t] = -1;
                    break;
                case LockEvent::Type::Release:
                case LockEvent::Type::ReleaseShared:
                    if( obtainAt[t] >= 0 && obtainAt[t] >= t0 && obtainAt[t] <= t1 )
                    {
                        const int64_t hold = time - obtainAt[t];
                        holdTotal += hold;
                        holdMax = std::max( holdMax, hold );
                    }
                    obtainAt[t] = -1;
                    break;
                }
            }
            if( acquired == 0 ) continue;

            const char* name = lock.customName.Active() ? w.GetString( lock.customName ) : w.GetZoneName( w.GetSourceLocation( lock.srcloc ) );
            WriteSlot( out_names, name_stride, n, name );
            out_threads[n] = uint32_t( lock.threadList.size() );
            out_acquired[n] = acquired;
            out_contended[n] = contendedCount;
            out_wait_total[n] = waitTotal;
            out_wait_max[n] = waitMax;
            out_hold_total[n] = holdTotal;
            out_hold_max[n] = holdMax;
            out_worst_wait_at[n] = worstAt;
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetMemoryPools( uint32_t capacity, char* out_names, uint32_t name_stride,
                                             uint64_t* out_allocations, uint64_t* out_live,
                                             uint64_t* out_bytes, uint64_t* out_peak )
{
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        uint32_t n = 0;
        for( const auto& entry : w.GetMemNameMap() )
        {
            if( n >= capacity ) break;
            const MemData& mem = *entry.second;
            if( mem.data.empty() ) continue;
            WriteSlot( out_names, name_stride, n, entry.first == 0 ? "default" : w.GetString( entry.first ) );
            out_allocations[n] = mem.data.size();
            out_live[n] = mem.active.size();
            out_bytes[n] = mem.usage;
            out_peak[n] = mem.plot ? uint64_t( mem.plot->max ) : mem.usage;
            n++;
        }
        return n;
    } );
}

TRACY_API uint32_t RefiiTracyGetMemoryLargest( const char* pool, uint32_t capacity, uint64_t* out_id,
                                               uint64_t* out_size, int64_t* out_time )
{
    if( !pool ) return 0;
    return WithWorker( [&]( Worker& w ) -> uint32_t {
        const MemData* found = nullptr;
        for( const auto& entry : w.GetMemNameMap() )
        {
            const char* name = entry.first == 0 ? "default" : w.GetString( entry.first );
            if( strcmp( name, pool ) == 0 ) { found = entry.second; break; }
        }
        if( !found ) return 0;

        std::vector<const MemEvent*> live;
        live.reserve( found->active.size() );
        for( const auto& active : found->active ) live.push_back( &found->data[active.second] );
        const size_t keep = std::min<size_t>( capacity, live.size() );
        std::partial_sort( live.begin(), live.begin() + keep, live.end(), []( const MemEvent* l, const MemEvent* r ) { return l->Size() > r->Size(); } );
        for( size_t i = 0; i < keep; i++ )
        {
            out_id[i] = live[i]->Ptr();
            out_size[i] = live[i]->Size();
            out_time[i] = live[i]->TimeAlloc();
        }
        return uint32_t( keep );
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
