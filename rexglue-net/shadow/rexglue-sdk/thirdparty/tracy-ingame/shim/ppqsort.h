#ifndef REFII_TRACY_PPQSORT_SHIM_H
#define REFII_TRACY_PPQSORT_SHIM_H

// REFII: the in-game Worker sorts with the pdqsort Tracy already ships rather than PPQSort's
// parallel sort, which would be one more downloaded dependency. Single-threaded: the Worker
// sorts on its own thread while a capture runs, and the game owns the cores.

#include "tracy_pdqsort.h"

namespace ppqsort
{
namespace execution
{
struct parallel_policy {};
inline constexpr parallel_policy par {};
}

template<typename Iter>
inline void sort( execution::parallel_policy, Iter begin, Iter end )
{
    tracy::pdqsort( begin, end );
}

template<typename Iter, typename Compare>
inline void sort( execution::parallel_policy, Iter begin, Iter end, Compare comp )
{
    tracy::pdqsort( begin, end, comp );
}
}

#endif
