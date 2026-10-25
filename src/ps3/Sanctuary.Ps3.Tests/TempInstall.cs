using System;
using System.Collections.Generic;
using System.IO;

namespace Sanctuary.Ps3.Tests;

/// <summary>A throw-away folder holding the files of a synthetic install.</summary>
internal sealed class TempInstall : IDisposable
{
    public TempInstall(IEnumerable<(string File, byte[] Data)> files)
    {
        Root = Path.Combine(Path.GetTempPath(), "sanctuary-ps3-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Root);

        foreach (var (file, data) in files)
        {
            var target = Path.Combine(Root, file);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a temp folder left behind is harmless.
        }
    }
}
