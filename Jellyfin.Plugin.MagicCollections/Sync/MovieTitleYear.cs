using System.Text;

namespace Jellyfin.Plugin.MagicCollections.Sync;

public readonly record struct MovieTitleYear(string Title, int Year)
{
    internal bool IsComplete => Year > 0 && !string.IsNullOrWhiteSpace(Title);

    internal MovieTitleYear MatchKey() => new(NormalizeTitle(Title).ToUpperInvariant(), Year);

    internal static string NormalizeTitle(string title) =>
        string.Join(' ', title.Normalize(NormalizationForm.FormC).Replace('’', '\'')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
