using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DesktopShrine.Storage;
using GOverlayPlugin.Interfaces;

namespace DesktopShrine.Plugin.GOverlay.LegacySdk;

internal static class GOverlayLcdCommandTrace
{
    private static readonly object WriterGate = new();
    private static readonly ConcurrentDictionary<long, string> Active = new();
    private static StreamWriter? writer;
    private static Action<string>? reportStatus;
    private static long commandSequence;
    private static long passSequence;
    private static int activeCommands;
    private static bool initializationAttempted;
    private static string tracePath = string.Empty;
    private static Exception? initializationFailure;

    public static string TracePath
    {
        get
        {
            EnsureInitialized();
            return tracePath;
        }
    }

    public static void EnsureInitialized(IHost? host = null)
    {
        var shouldReport = host is not null;
        if (host is not null)
            reportStatus = host.DebugMessage;

        lock (WriterGate)
        {
            if (initializationAttempted)
            {
                if (shouldReport)
                    ReportInitializationStatus();
                return;
            }
            initializationAttempted = true;

            var configured = Environment.GetEnvironmentVariable(
                DesktopShrinePaths.GOverlayTraceEnvironmentVariable);
            try
            {
                tracePath = DesktopShrinePaths.Current
                    .ResolveGOverlayCommandTraceFile(configured);
                var directory = Path.GetDirectoryName(tracePath)
                    ?? throw new InvalidOperationException(
                        "The LCD command trace path has no parent directory.");
                Directory.CreateDirectory(directory);
                var stream = new FileStream(
                    tracePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.SequentialScan);
                writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                var timestamp = DateTime.UtcNow.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture);
                var assemblyVersion = typeof(GOverlayLcdCommandTrace)
                    .Assembly
                    .GetName()
                    .Version?
                    .ToString() ?? "unknown";
                WriteLocked(
                    $"TRACE INITIALIZED timestamp={timestamp} pid={Process.GetCurrentProcess().Id} assemblyVersion={Token(assemblyVersion)} path={Quote(tracePath)} timing=Stopwatch payloadEstimate=logical-managed-arguments autoFlush=true");
                writer.Flush();
            }
            catch (Exception error)
            {
                initializationFailure = error;
                writer?.Dispose();
                writer = null;
            }

            if (shouldReport)
                ReportInitializationStatus();
        }
    }

    public static long NextPassId() =>
        Interlocked.Increment(ref passSequence);

    public static void RenderStart(
        long passId,
        int threadId,
        string source,
        string details)
        => Write(
            $"RENDER START pass={passId} thread={threadId} source={Token(source)} {details}");

    public static void RenderEnd(
        long passId,
        int threadId,
        string status,
        int commandCount,
        long estimatedBytes,
        long elapsedTicks,
        long lastSequence)
        => Write(
            $"RENDER END pass={passId} thread={threadId} status={Token(status)} commands={commandCount} estimatedBytes={estimatedBytes} elapsedMs={ElapsedMilliseconds(elapsedTicks)} lastSeq={lastSequence}");

    public static void Event(string eventName, string details)
        => Write($"EVENT name={Token(eventName)} {details}");

    public static CommandToken Before(
        long passId,
        string region,
        string operation,
        string details,
        long estimatedBytes)
    {
        var sequence = Interlocked.Increment(ref commandSequence);
        var threadId = Environment.CurrentManagedThreadId;
        var startTimestamp = Stopwatch.GetTimestamp();
        var active = Interlocked.Increment(ref activeCommands);
        Active[sequence] =
            $"seq={sequence}:pass={passId}:thread={threadId}:region={Token(region)}:op={Token(operation)}";

        Write(
            $"LCDCMD BEFORE seq={sequence} pass={passId} thread={threadId} region={Token(region)} op={Token(operation)} active={active} estimatedBytes={estimatedBytes} {details}");
        if (active > 1)
        {
            var overlapping = string.Join(
                ",",
                Active.Where(pair => pair.Key != sequence)
                    .OrderBy(pair => pair.Key)
                    .Select(pair => pair.Value));
            Write(
                $"LCDCMD CONCURRENCY active={active} seq={sequence} overlapping={Quote(overlapping)}");
        }

        return new(
            sequence,
            passId,
            threadId,
            region,
            operation,
            startTimestamp,
            estimatedBytes);
    }

    public static void After(CommandToken token, bool result)
    {
        var elapsed = Stopwatch.GetTimestamp() - token.StartTimestamp;
        Write(
            $"LCDCMD AFTER seq={token.Sequence} pass={token.PassId} thread={Environment.CurrentManagedThreadId} beginThread={token.ThreadId} region={Token(token.Region)} op={Token(token.Operation)} result={result} elapsedMs={ElapsedMilliseconds(elapsed)}");
        Complete(token.Sequence);
    }

    public static void Error(CommandToken token, Exception error)
    {
        var elapsed = Stopwatch.GetTimestamp() - token.StartTimestamp;
        Write(
            $"LCDCMD ERROR seq={token.Sequence} pass={token.PassId} thread={Environment.CurrentManagedThreadId} beginThread={token.ThreadId} region={Token(token.Region)} op={Token(token.Operation)} elapsedMs={ElapsedMilliseconds(elapsed)} exceptionType={Token(error.GetType().FullName ?? error.GetType().Name)} exception={Quote(error.ToString())}");
        Complete(token.Sequence);
    }

    public static string Quote(string? value, int maximumLength = 240)
    {
        var sanitized = (value ?? string.Empty)
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t")
            .Replace("\"", "\\\"");
        if (sanitized.Length > maximumLength)
            sanitized = sanitized.Substring(0, maximumLength) + "...";
        return "\"" + sanitized + "\"";
    }

    public static string ShortIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "none";
        return Token(value!.Length <= 16 ? value : value.Substring(0, 16));
    }

    internal static void ResetForTests()
    {
        lock (WriterGate)
        {
            writer?.Dispose();
            writer = null;
            reportStatus = null;
            commandSequence = 0;
            passSequence = 0;
            activeCommands = 0;
            initializationAttempted = false;
            initializationFailure = null;
            tracePath = string.Empty;
            Active.Clear();
        }
    }

    private static void Complete(long sequence)
    {
        _ = Active.TryRemove(sequence, out _);
        _ = Interlocked.Decrement(ref activeCommands);
    }

    private static void ReportInitializationStatus()
    {
        if (reportStatus is null)
            return;
        if (writer is not null)
            SafeReport(
                "Desktop Shrine - LCD command trace path: " + tracePath);
        else if (initializationFailure is not null)
            SafeReport(
                "Desktop Shrine - LCD command trace FAILED: "
                + initializationFailure);
    }

    private static void Write(string message)
    {
        EnsureInitialized();
        lock (WriterGate)
        {
            try
            {
                WriteLocked(message);
            }
            catch (Exception error)
            {
                SafeReport(
                    "Desktop Shrine - LCD command trace write failed: " + error);
            }
        }
    }

    private static void SafeReport(string message)
    {
        try
        {
            reportStatus?.Invoke(message);
        }
        catch
        {
            // Diagnostic reporting must not affect the renderer.
        }
    }

    private static void WriteLocked(string message)
    {
        writer?.WriteLine(
            DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture)
            + " "
            + message);
    }

    private static string ElapsedMilliseconds(long elapsedTicks) =>
        (elapsedTicks * 1000d / Stopwatch.Frequency).ToString(
            "0.000",
            CultureInfo.InvariantCulture);

    private static string Token(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "none" : value!;
        var result = new StringBuilder(source.Length);
        foreach (var character in source)
            result.Append(char.IsLetterOrDigit(character)
                          || character is '-' or '_' or '.' or '/'
                ? character
                : '_');
        return result.ToString();
    }

    internal sealed class CommandToken(
        long sequence,
        long passId,
        int threadId,
        string region,
        string operation,
        long startTimestamp,
        long estimatedBytes)
    {
        public long Sequence { get; } = sequence;
        public long PassId { get; } = passId;
        public int ThreadId { get; } = threadId;
        public string Region { get; } = region;
        public string Operation { get; } = operation;
        public long StartTimestamp { get; } = startTimestamp;
        public long EstimatedBytes { get; } = estimatedBytes;
    }
}
