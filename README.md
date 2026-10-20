# PCL Patcher Web

Browser port of [pcl-patcher](https://github.com/Ryorin1337/pcl-patcher), originally created by **Ryorin1337**. [l5z12/pcl](https://github.com/l5z12/pcl) provides the web version and hosting.

Supports **PCL2 2.99.0.0**. Select your executable, apply the patch, and download the result. Files stay on your device.

## Build

Requires .NET 10 SDK, Node.js 22+, and pnpm.

```sh
pnpm install
pnpm build
pnpm dev
```

Static output: `dist/`. Local preview: `http://127.0.0.1:5173`.

## Deploy

Push to `main` to deploy to **https://pcl.l5z12.dev** with GitHub Actions.
Add repository secrets `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_API_TOKEN` ([setup](https://developers.cloudflare.com/workers/ci-cd/external-cicd/github-actions/)).
Use an Edit Cloudflare Workers token scoped to your account and the `l5z12.dev` zone. The zone must be active in that account.

[MIT license](LICENSE). Original patcher © 2026 Ryorin1337; web port © 2026 l5z12.
