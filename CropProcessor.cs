using System;
using System.IO;
using System.Threading.Tasks;
using WinUIShared.Controls;
using WinUIShared.Helpers;

namespace VideoCropperPage
{
    public class CropProcessor(string ffmpegPath): Processor(ffmpegPath, VideoCropper.App.Diagnostics)
    {
        public event Action<TimeSpan>? ProcessingPositionChanged;
        public bool Succeeded { get; private set; }

        public async Task Crop(string fileName, string ffmpegPath, string x, string y, string width, string height,
            bool normalizePixelAspectRatio, int quarterTurns, TimeSpan inPosition, TimeSpan outPosition)
        {
            Succeeded = false;
            var range = new CropTimeRange(inPosition, outPosition);
            ProcessingPositionChanged?.Invoke(range.In);
            progressPrimary.Report(0);
            centerTextPrimary.Report("0.0 %");
            var cropParams = $"crop={width}:{height}:{x}:{y}";
            cropParams = quarterTurns switch
            {
                1 => "transpose=clock," + cropParams,
                2 => "hflip,vflip," + cropParams,
                3 => "transpose=cclock," + cropParams,
                _ => cropParams
            };
            if (normalizePixelAspectRatio) cropParams += ",scale=trunc(iw*sar/2)*2:ih,setsar=1";
            switch (gpuInfo?.Vendor)
            {
                case GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel:
                    var gpuPixelFormat = await GetGpuPixelFormat(fileName);
                    var (hwDownArgs, hwUpArgs) = GpuInfo.FilterParams(gpuInfo, gpuPixelFormat);
                    cropParams = $"{hwDownArgs}{cropParams}{hwUpArgs}";
                    break;
            }
            var succeeded = await StartFfmpegTranscodingProcessDefaultQuality([fileName], GetOutputName(fileName),
                range.InputArguments, $"{range.OutputArguments} -vf \"{cropParams}\"", (_, currentTime, _, _, fps, speed) =>
            {
                var elapsed = TimeSpan.FromTicks(Math.Clamp(currentTime.Ticks, 0, range.Duration.Ticks));
                IncrementProgress(elapsed / range.Duration * ProgressMax, fps, (range.Duration - elapsed) / speed);
                ProcessingPositionChanged?.Invoke(range.SourcePosition(currentTime));
            });
            if (HasBeenKilled()) return;
            Succeeded = succeeded;
            if (!succeeded)
                VideoCropper.App.Diagnostics.Report("FFmpeg crop failed", $"Input: {fileName}; output: {outputFile}; see session log for command and exit code");
            AllDone();
        }

        private string GetOutputName(string path)
        {
            var inputName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var parentFolder = Path.GetDirectoryName(path) ?? throw new FileNotFoundException($"The specified path does not exist: {path}");
            outputFile = Path.Combine(parentFolder, $"{inputName}_CROPPED{extension}");
            File.Delete(outputFile);
            return outputFile;
        }

        private void IncrementProgress(double progress, double fps, TimeSpan estimatedTimeRemaining)
        {
            progressPrimary.Report(progress);
            centerTextPrimary.Report($"{progress:F2} % · {fps:F1} fps");
            rightTextPrimary.Report($"ETA {estimatedTimeRemaining:hh\\:mm\\:ss}");
        }

        private void AllDone()
        {
            progressPrimary.Report(ProgressMax);
            centerTextPrimary.Report("100 %");
        }
    }
}
