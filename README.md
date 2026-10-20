# PCL Patcher Web

Browser port of [pcl-patcher](https://github.com/Ryorin1337/pcl-patcher), originally created by **Ryorin1337**. [l5z12/pcl](https://github.com/l5z12/pcl) provides the web version and hosting.

Supports **PCL2 2.99.0.0** and **PCL CE 2.15.1**. Select your executable, apply the patch, and download the result. Files stay on your device.

- **PCL2 2.99.0.0**: restores Minecraft downloads, the Minecraft card and modpack installation (five patches, from pcl-patcher).
- **PCL CE 2.15.1**: removes the region lock added in [PCL-CE#3673](https://github.com/PCL-Community/PCL-CE/pull/3673) and [#3676](https://github.com/PCL-Community/PCL-CE/pull/3676) by making `RestrictedFeaturePolicy` allow every feature. That covers Minecraft downloads, installs, repairs, resets, loader installs, modpack installs and the file check at launch. PCL CE ships as a .NET single-file bundle; only `Plain Craft Launcher 2.dll` inside it changes. Updating PCL CE replaces the patched file with the official build.

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
