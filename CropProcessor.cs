using System;
using System.IO;
using System.Threading.Tasks;
using WinUIShared.Controls;
using WinUIShared.Helpers;

namespace VideoCropperPage
{
    public class CropProcessor(string ffmpegPath): Processor(ffmpegPath, new FileLogger.FileLogger("ReelBox/Crop"))
    {
        public async Task Crop(string fileName, string ffmpegPath, string x, string y, string width, string height,
            bool normalizePixelAspectRatio)
        {
            progressPrimary.Report(0);
            centerTextPrimary.Report("0.0 %");
            var cropParams = $"crop={width}:{height}:{x}:{y}";
            if (normalizePixelAspectRatio) cropParams += ",scale=trunc(iw*sar/2)*2:ih,setsar=1";
            switch (gpuInfo?.Vendor)
            {
                case GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel:
                    var gpuPixelFormat = await GetGpuPixelFormat(fileName);
                    var (hwDownArgs, hwUpArgs) = GpuInfo.FilterParams(gpuInfo, gpuPixelFormat);
                    cropParams = $"{hwDownArgs}{cropParams}{hwUpArgs}";
                    break;
            }
            await StartFfmpegTranscodingProcessDefaultQuality([fileName], GetOutputName(fileName), $"-vf \"{cropParams}\"", (progress, currentTime, duration, _, fps, speed) =>
            {
                IncrementProgress(progress, fps, (duration - currentTime) / speed);
            });
            if (HasBeenKilled()) return;
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
