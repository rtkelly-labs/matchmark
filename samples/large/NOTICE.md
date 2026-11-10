# Large specs: sources and licences

The specs here are unmodified copies of third-party files, kept so every run of `LargeSpecTests` and the benchmarks reads
the same bytes. `upstream.tsv` pins each one to an upstream commit and its SHA-256.

| File | Source | Licence |
|---|---|---|
| `github.json` | [github/rest-api-description](https://github.com/github/rest-api-description), `descriptions/api.github.com/api.github.com.json` | MIT, Copyright (c) 2020 GitHub |
| `stripe.json` | [stripe/openapi](https://github.com/stripe/openapi), `openapi/spec3.json` | MIT, Copyright (c) 2011- Stripe, Inc. |

Both are distributed under the MIT License:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
> rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
> persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
> Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
> WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
> COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Keeping them current

A weekly job in `large-specs.yml` runs `dotnet run samples/large/Refresh.cs -- --check`. When upstream has changed a
spec since its pin, it opens an issue (or updates the open one) titled "Large specs changed upstream" with links to each
upstream diff. To take the change, run `dotnet run samples/large/Refresh.cs` and open a pull request: its large-spec
run shows what the new version does to the ledger.
