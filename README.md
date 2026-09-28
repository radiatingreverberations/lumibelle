# Lumibelle

A personal video studio for growing an idea into a film. Write a screenplay, turn its cast and locations into reference images, compose and generate shots, and cut them together.

**Manual, screenshots and examples: [lumibelle.ai](https://lumibelle.ai)** (sources in [docs/manual](docs/manual/README.md)).

## Run from source

Install the .NET 10 SDK selected by `global.json`, then run from the repository root:

```powershell
dotnet restore lumibelle.slnx
dotnet run --project src/Lumibelle.Web --launch-profile http
```

Open [http://localhost:5183](http://localhost:5183) and create a project. Manual writing, asset editing and cutting need no AI backend; see [AI setup](docs/manual/ai-setup.md) to connect one.

When run from source, projects are stored in the repository's ignored `App_Data/Projects`. To use another library, see [Projects and storage](docs/manual/projects.md).

## Development

Built with a shared .NET 10 core and Razor UI, an Interactive Server web host, and a MAUI desktop feasibility host. Testing, frontend builds and implementation notes are in [docs/development.md](docs/development.md). Architecture, desktop hosts and publishing are covered in [shared hosts and distribution](docs/shared-hosts.md). For UI work, use the [UX guidelines](docs/ux-guidelines.md). They describe the intended design.
