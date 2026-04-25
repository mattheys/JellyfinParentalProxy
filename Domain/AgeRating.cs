namespace Domain;

/// <summary>
/// Ordered age-rating scale. Lower values are more permissive.
/// Unrated sits at the top so that items with no rating are treated strictly.
/// </summary>
public enum AgeRating
{
    G      = 1,
    PG     = 2,
    PG13   = 3,
    R      = 4,
    NC17   = 5,
    Unrated = 6,
}

public static class AgeRatingParser
{
    public static AgeRating Parse(string? rating) =>
        rating?.ToUpperInvariant() switch
        {
            "G" or "TV-Y" or "TV-G" or "TV-Y7" => AgeRating.G,
            "PG" or "TV-PG"                     => AgeRating.PG,
            "PG-13" or "TV-14"                  => AgeRating.PG13,
            "R" or "TV-MA"                      => AgeRating.R,
            "NC-17" or "X"                      => AgeRating.NC17,
            _                                   => AgeRating.Unrated,
        };
}
