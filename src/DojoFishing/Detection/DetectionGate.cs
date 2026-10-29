namespace DojoFishing.Detection;

public sealed class DetectionGate(Settings settings)
{
    private int above, below;
    public bool Armed { get; private set; } = true;
    public void RequireRelease() { Armed = false; above = below = 0; }
    public bool Update(double score, double db, bool busy)
    {
        if (busy) { above = below = 0; return false; }
        if (!Armed)
        {
            below = score < settings.ReleaseThreshold ? below + 1 : 0;
            if (below * 10 >= settings.ReleaseMs) { Armed = true; below = 0; }
            return false;
        }
        above = score >= settings.Threshold && db >= settings.MinimumDb ? above + 1 : 0;
        if (above < settings.ConfirmFrames) return false;
        RequireRelease();
        return true;
    }
}
