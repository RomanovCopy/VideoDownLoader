using VideoDownLoader.Models;
using VideoDownLoader.Services;

namespace VideoDownLoader.Tests;

public sealed class JsonStorageServiceTests
{
    [Fact]
    public void Settings_RoundTripPreservesImageSearchParametersAndFavorites()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"VideoDownLoader.Tests-{Guid.NewGuid():N}");

        try
        {
            var storage = new JsonStorageService(dataDirectory);
            storage.SaveSettings(new ApplicationSettings
            {
                ImageOutputDirectory = @"D:\Images",
                LastWebsiteUrl = "https://example.test/gallery",
                ImageQualityPresetIndex = 2,
                ImageScanDepth = 3,
                ImageAccessMode = 1,
                FavoriteWebsiteUrls =
                [
                    "https://example.test/gallery",
                    "https://images.example.test/search?q=sea"
                ]
            });

            var restored = storage.LoadSettings();

            Assert.Equal(@"D:\Images", restored.ImageOutputDirectory);
            Assert.Equal("https://example.test/gallery", restored.LastWebsiteUrl);
            Assert.Equal(2, restored.ImageQualityPresetIndex);
            Assert.Equal(3, restored.ImageScanDepth);
            Assert.Equal(1, restored.ImageAccessMode);
            Assert.Equal(2, restored.FavoriteWebsiteUrls.Count);
            Assert.Contains("https://images.example.test/search?q=sea", restored.FavoriteWebsiteUrls);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
