using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.MagicCollections;

/// <summary>
/// The small subset of the platform ICU C API needed for bidirectional titles.
/// HarfBuzz shapes a directional run but doesn't implement the Unicode Bidi Algorithm.
/// </summary>
internal sealed class PosterBidi : IDisposable
{
    private const byte DefaultLeftToRight = 0xfe; // UBIDI_DEFAULT_LTR: use the first strong character.
    private static readonly Lazy<Icu?> PlatformIcu = new(Icu.Load);
    private readonly Icu _icu;
    private readonly IntPtr _paragraph;
    private readonly IntPtr _line;
    private GCHandle _title;

    public static PosterBidi? ForTitle(string title) =>
        !string.IsNullOrEmpty(title) && !System.Text.Ascii.IsValid(title) && PlatformIcu.Value is { } icu
            ? new PosterBidi(title, icu)
            : null;

    private PosterBidi(string title, Icu icu)
    {
        _icu = icu;
        _title = GCHandle.Alloc(title, GCHandleType.Pinned);
        try
        {
            _paragraph = icu.Open();
            _line = icu.Open();
            if (_paragraph == IntPtr.Zero || _line == IntPtr.Zero)
            {
                throw new OutOfMemoryException("Could not allocate ICU bidirectional text layout.");
            }

            var error = 0;
            icu.SetParagraph(_paragraph, _title.AddrOfPinnedObject(), title.Length, DefaultLeftToRight, IntPtr.Zero, ref error);
            ThrowOnError(error);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Run[] VisualRuns(int start, int end)
    {
        var error = 0;
        _icu.SetLine(_paragraph, start, end, _line, ref error);
        ThrowOnError(error);
        var count = _icu.CountRuns(_line, ref error);
        ThrowOnError(error);
        var runs = new Run[count];
        for (var i = 0; i < count; i++)
        {
            var direction = _icu.GetVisualRun(_line, i, out var offset, out var length);
            runs[i] = new(start + offset, start + offset + length, direction == 1);
        }

        return runs;
    }

    public void Dispose()
    {
        if (_title.IsAllocated)
        {
            _icu.Close(_line);
            _icu.Close(_paragraph);
            _title.Free();
        }
    }

    private static void ThrowOnError(int error)
    {
        if (error > 0)
        {
            throw new InvalidOperationException($"ICU bidirectional text layout failed (error {error}).");
        }
    }

    internal readonly record struct Run(int Start, int End, bool RightToLeft);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenBidi();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseBidi(IntPtr bidi);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBidiParagraph(IntPtr bidi, IntPtr text, int length, byte level, IntPtr levels, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetBidiLine(IntPtr paragraph, int start, int end, IntPtr line, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountBidiRuns(IntPtr bidi, ref int error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBidiVisualRun(IntPtr bidi, int run, out int offset, out int length);

    private sealed record Icu(
        OpenBidi Open, CloseBidi Close, SetBidiParagraph SetParagraph, SetBidiLine SetLine,
        CountBidiRuns CountRuns, GetBidiVisualRun GetVisualRun)
    {
        public static Icu? Load()
        {
            if (OperatingSystem.IsWindows())
            {
                return TryLoad("icu.dll", DllImportSearchPath.System32) ?? TryLoad("icuuc.dll", DllImportSearchPath.System32);
            }

            if (OperatingSystem.IsMacOS())
            {
                return TryLoad("/usr/lib/libicucore.A.dylib");
            }

            var unversioned = TryLoad("libicuuc.so");
            if (unversioned is not null)
            {
                return unversioned;
            }

            // Linux runtime packages generally ship only the versioned soname, not the
            // unversioned development symlink. The official 10.11.5 image has ICU 76.
            for (var version = 99; version >= 60; version--)
            {
                var icu = TryLoad($"libicuuc.so.{version}", suffix: $"_{version}");
                if (icu is not null)
                {
                    return icu;
                }
            }

            return null;
        }

        private static Icu? TryLoad(string name, DllImportSearchPath? path = null, string? suffix = null)
        {
            if (!NativeLibrary.TryLoad(name, typeof(PosterBidi).Assembly, path, out var library))
            {
                return null;
            }

            T? Find<T>(string symbol) where T : Delegate =>
                NativeLibrary.TryGetExport(library, symbol + suffix, out var export)
                    ? Marshal.GetDelegateForFunctionPointer<T>(export)
                    : null;

            var open = Find<OpenBidi>("ubidi_open");
            var close = Find<CloseBidi>("ubidi_close");
            var setParagraph = Find<SetBidiParagraph>("ubidi_setPara");
            var setLine = Find<SetBidiLine>("ubidi_setLine");
            var countRuns = Find<CountBidiRuns>("ubidi_countRuns");
            var getVisualRun = Find<GetBidiVisualRun>("ubidi_getVisualRun");
            if (open is not null && close is not null && setParagraph is not null && setLine is not null
                && countRuns is not null && getVisualRun is not null)
            {
                // Keep the native library loaded for the delegates' lifetime.
                return new(open, close, setParagraph, setLine, countRuns, getVisualRun);
            }

            NativeLibrary.Free(library);
            return null;
        }
    }
}
