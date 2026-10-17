#ifndef REFII_TRACY_INTERNAL_HPP
#define REFII_TRACY_INTERNAL_HPP

#include <mutex>

namespace tracy
{
class Worker;
}

namespace refii_tracy
{

// The held capture, shared by the capture calls and the queries. Read HeldWorker only under
// CaptureLock(): Discard frees the Worker under it.
std::mutex& CaptureLock();
tracy::Worker* HeldWorker();

}

#endif
