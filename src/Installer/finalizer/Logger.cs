// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.DotNet.Finalizer;

internal static class Logger
{
    private static StreamWriter s_logStream = StreamWriter.Null;

    public static void Init(StreamWriter logStream) => s_logStream = logStream;

    public static void Log(string message)
    {
        var pid = Environment.ProcessId;
        var tid = Environment.CurrentManagedThreadId;
        s_logStream.WriteLine($"[{pid:X4}:{tid:X4}][{DateTime.Now:yyyy-MM-ddTHH:mm:ss}] Finalizer: {message}");
    }
}
