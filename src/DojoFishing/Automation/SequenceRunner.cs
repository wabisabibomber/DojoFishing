namespace DojoFishing.Automation;

public abstract record ActionStep;
public sealed record LeftClickStep : ActionStep;
public sealed record DelayStep(int Milliseconds) : ActionStep;

public sealed class SequenceRunner(int targetPid, Action<string> log, Action? click = null, Func<bool>? foreground = null)
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private Task running = Task.CompletedTask;
    private bool busy;
    public bool Busy { get { lock (sync) return busy; } }
    public event Action? Completed;
    public bool TryStart(IReadOnlyList<ActionStep> steps)
    {
        lock (sync)
        {
            if (busy || cancellation.IsCancellationRequested) return false;
            busy = true;
            running = Task.Run(async () =>
            {
                try
                {
                    log("シーケンス開始");
                    foreach (var step in steps)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        switch (step)
                        {
                            case LeftClickStep:
                                lock (sync)
                                {
                                    cancellation.Token.ThrowIfCancellationRequested();
                                    if (!(foreground?.Invoke() ?? NativeInput.IsTargetForeground(targetPid)))
                                        throw new InvalidOperationException("対象アプリが最前面ではないため中断しました。");
                                    (click ?? NativeInput.Click)();
                                    log("左クリック送信");
                                }
                                break;
                            case DelayStep delay: await Task.Delay(delay.Milliseconds, cancellation.Token); break;
                        }
                    }
                    log("シーケンス完了");
                }
                catch (OperationCanceledException) { log("シーケンス停止"); }
                catch (Exception ex) { log(ex.Message); }
                finally
                {
                    Completed?.Invoke();
                    lock (sync) busy = false;
                }
            });
            return true;
        }
    }
    public async Task StopAsync()
    {
        Task task;
        lock (sync) { cancellation.Cancel(); task = running; }
        await task;
    }
    public Task WaitForCompletionAsync() { lock (sync) return running; }
}
