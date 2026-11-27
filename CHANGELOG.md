# Changelog

## v0.3.0.0 (2026-11-27)

### Feat

- **sync**: tag collections with their source and list URL
- **posters**: use source list artwork as the background
- **posters**: add source favicons to collection posters
- **bfi**: sync editorial lists by movie title and year

### Fix

- **bfi**: include the site favicon on collection posters
- **posters**: skip branded MDBList artwork for backgrounds
- **posters**: normalize artwork before applying an 80% black overlay
- **bfi**: use Open Graph metadata for collection descriptions
- **bfi**: distinguish captioned lists from other articles

## v0.2.3.0 (2026-11-26)

### Feat

- **letterboxd**: move cache clearing to a manual task

## v0.2.2.0 (2026-11-26)

### Feat

- **letterboxd**: cache film IDs until manually cleared

## v0.2.1.0 (2026-11-26)

### Feat

- **sync**: copy source list descriptions to collection overviews (#25)

### Fix

- **sync**: make Letterboxd list trailing slash optional (#26)
- **sync**: read list descriptions without changing source markup (#25)
- **sync**: accept Letterboxd film aliases in public lists (#26)

## v0.2.0.0 (2026-11-26)

### Feat

- **sync**: support Steven Lu Popular Movies feeds
- **sync**: support public Letterboxd lists
- **collections**: generate missing collection posters
- **catalog**: add Magic Collections logo
- **docker**: preconfigure the Jellyfin test sandbox

### Fix

- **sync**: verify Letterboxd membership and media types (#14)
- **sync**: persist Popular Movies polling across restarts
- **collections**: render readable Unicode posters and preserve existing art

## v0.1.1.0 (2026-11-26)
