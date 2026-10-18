using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.JellyBridge.JellyfinModels;

/// <summary>
/// Wrapper around Jellyfin's Season class.
/// Provider IDs and the TMDB ID come from the parent series, since Jellyseerr requests seasons by the series TMDB ID.
/// </summary>
public class JellyfinSeason : WrapperBase<Season>, IJellyfinItem
{
    public BaseItemKind TypeName => BaseItemKind.Season;

    public JellyfinSeason(Season season, JellyfinSeries series) : base(season)
    {
        Series = series;
        InitializeVersionSpecific();
    }

    /// <summary>
    /// Wraps a season, or returns null when it has no season number or parent series.
    /// </summary>
    public static JellyfinSeason? FromSeason(Season season)
    {
        if (season.IndexNumber is not int || season.Series is not Series series)
        {
            return null;
        }

        return new JellyfinSeason(season, JellyfinSeries.FromSeries(series));
    }

    public JellyfinSeries Series { get; }

    public int SeasonNumber => Inner.IndexNumber ?? 0;

    public Guid Id => Inner.Id;

    public string Name => $"{Series.Name} - {Inner.Name}";

    public string Path => Inner.Path;

    public Dictionary<string, string> ProviderIds => Series.ProviderIds;

    public int? GetTmdbId() => Series.GetTmdbId();

    public string? GetProviderId(string name) => Series.GetProviderId(name);

    public bool ItemsMatch(IJellyfinItem other) => other != null && Id == other.Id;

    public string? ToJson(MediaBrowser.Controller.Dto.IDtoService dtoService)
    {
        try
        {
            var baseItemDto = dtoService.GetBaseItemDto(Inner, new MediaBrowser.Controller.Dto.DtoOptions());
            return System.Text.Json.JsonSerializer.Serialize(baseItemDto);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
