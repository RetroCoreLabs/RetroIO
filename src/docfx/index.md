# RetroIO

Storage, filesystems and communications for retro computing: forty-four NuGet packages that read
and write the disk formats, and speak the network protocols, of machines built between roughly 1975
and 1995.

The source, the build instructions and the tools are on
[GitHub](https://github.com/RetroCoreLabs/RetroIO).

## Get started

```powershell
dotnet add package RetroIO.Storage.Ndfs
```

## Where to look

 - **Storage** (`RetroIO.Storage.*`): block devices, the filesystem abstraction, the format
   drivers and NDFS. Start with the `RetroIO.Storage.Core` namespace.
 - **Protocols** (`RetroIO.Protocols.*`): TNFS, Econet, X.25 and XMSG.
 - **Link layer** (`RetroIO.Hdlc`, `RetroIO.Lapb`, `RetroIO.Link`): shared by the wire protocols.
 - **Cross-cutting** (`RetroIO.Identity`, `RetroIO.Security.*`, `RetroIO.Management.*`,
   `RetroIO.Server.*`).

See the [API reference](api/index.md) for every namespace, type and member.
