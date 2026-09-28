# Getting started

## Run from source

Install the .NET 10 SDK selected by the repository's `global.json`, then run from the repository root:

```powershell
dotnet restore lumibelle.slnx
dotnet run --project src/Lumibelle.Web --launch-profile http
```

Open [http://localhost:5183](http://localhost:5183). The app is intended for personal use on your computer.

FFmpeg/FFprobe are external dependencies used for video frames and MP4 export. Configure absolute executable paths in **AI settings** if Lumibelle does not inherit your terminal's `PATH`.

## Create a project

Create a project from the library, then open its **Script** or **Assets** studio. Projects, writing, asset metadata and reference images remain available after restarting the app.

Manual writing, asset editing and cutting require no AI backend. To use AI assistance or generate images and video, see [AI setup](ai-setup.md).

## Where your work is saved

Projects are saved on the computer running Lumibelle, not in browser storage. See [Projects and storage](projects.md) to choose where the library lives and how to back it up.
