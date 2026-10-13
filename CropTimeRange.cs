using System;
using System.Globalization;

namespace VideoCropperPage;

internal readonly record struct CropTimeRange(TimeSpan In, TimeSpan Out)
{
    public TimeSpan Duration => Out - In;
    public string InputArguments => $"-ss {In.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture)}";
    // Discard copied audio packets before the seek point as well as limiting output duration.
    public string OutputArguments => $"-ss 0 -t {Duration.TotalSeconds.ToString("0.#######", CultureInfo.InvariantCulture)}";
    public TimeSpan SourcePosition(TimeSpan encodedTime) =>
        In + TimeSpan.FromTicks(Math.Clamp(encodedTime.Ticks, 0, Duration.Ticks - 1));
}
