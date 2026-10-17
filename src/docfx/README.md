# DocFX configuration

This folder builds the API reference site for RetroIO. The site is published to GitHub Pages by
`.github/workflows/docs.yml` on every push to `main`.

## Build locally

```powershell
# one time
dotnet tool install -g docfx

# from the repository root; add --serve to preview at http://localhost:8080/
docfx src/docfx/docfx.json
```

## Layout

 - `docfx.json`: scans every `RetroIO.*/src/*.csproj` (tests and tools are not included) and writes
   YAML metadata into `api/`, then renders the site into `_site/`.
 - `index.md`: the landing page.
 - `toc.yml`: top-level navigation.
 - `api/index.md`: the API reference landing page.

`_site/` and `api/*.yml` are generated and git-ignored.

## Live site

<https://retrocorelabs.github.io/RetroIO/>
