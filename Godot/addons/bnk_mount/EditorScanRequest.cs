using System;

namespace Refii.EditorTools;

public sealed class EditorScanRequest
{
    public bool Pending { get; private set; }

    public void Request() => Pending = true;

    public void Cancel() => Pending = false;

    public void TryRun(bool scanning, bool importing, Action scan)
    {
        if (!Pending || scanning || importing) return;
        Pending = false;
        scan();
    }
}
