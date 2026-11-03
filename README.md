# NEX Launcher

NEX is an original, lightweight Windows launcher shell for Minecraft. It targets Windows 10 and 11 with .NET 8 WPF. The current build is a UI foundation; it does not claim to implement official account authentication, downloading every historical game version, mod loaders, skin uploads, or public server hosting.

## Run

Install the .NET 8 SDK on Windows, then run from this directory:

```powershell
dotnet run
```

To build a Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## Current functions

- A centered NEX splash screen that transitions to the launcher after three seconds.
- Chinese navigation for the dashboard, versions, mods and modpacks, resource packs, skins, multiplayer, quick server, and settings.
- Local Minecraft folder access and a server launch action for a user-provided `server.jar` with Java installed.
- Server address copy action for sharing an address in game.

## Boundaries and next steps

- Microsoft account sign-in and game launch must use the official authentication flow and a valid licensed account; they are not implemented in this shell.
- Version, mod, modpack, resource-pack, and skin catalogs need trusted provider integrations, compatibility checks, and download/install flows.
- Quick server starts a local `server.jar` only when the user has supplied it and accepted its EULA. Public access depends on the server host, firewall, router/NAT port forwarding, and ISP; a launcher cannot guarantee a public IP or successful reachability.
- The SVG in `Assets/nex-mark.svg` is the original NEX mark. Packaging it as a native Windows `.ico` can be added to the release pipeline.
