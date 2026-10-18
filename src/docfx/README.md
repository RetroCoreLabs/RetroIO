# DocFX configuration

This folder builds the documentation site for RetroIO: the API reference, the README of every package, the
longer guides and the tools. The site is published to GitHub Pages by `.github/workflows/docs.yml` on every
push to `main`.

## Build locally

```powershell
# one time
dotnet tool install -g docfx

# from src/ (the workflow does the same); add --serve to preview at http://localhost:8080/
docfx docfx/docfx.json --warningsAsErrors
```

The build must end with `Build succeeded.`, 0 warnings and 0 errors. A warning is a broken link or a
missing comment on the live site.

## The menu is generated

The list of package READMEs, guides, tools and the changelog (`packages/toc.yml`) is written from the files
git tracks, by `gen_toc.py`. The policy for what is on the site and what stays off is in that script's header.

```powershell
python src/docfx/gen_toc.py            # rewrite packages/toc.yml
python src/docfx/gen_toc.py --check    # CI runs this; it fails when the file is stale
```

Add a package or a guide, run the first command, and commit the result.

## Layout

 - `docfx.json`: scans every `RetroIO.*/src/*.csproj` (tests and tools are not included) and writes
   YAML metadata into `api/`, brings the READMEs in as pages, then renders the site into `_site/`.
 - `index.md`: the landing page.
 - `toc.yml`: top-level navigation (the header menu), ending with the link to the GitHub repository.
 - `packages/`: the landing page of the guides, and the generated `toc.yml` beside it.
 - `api/index.md`: the API reference landing page.
 - `gen_toc.py`: writes `packages/toc.yml`.

The icon in the header and the browser tab is `src/eng/nuget-icon.png`, the icon of the NuGet packages,
published as `images/nuget-icon.png`.

`_site/` and `api/*.yml` are generated and git-ignored.

## Live site

<https://retrocorelabs.github.io/RetroIO/>
