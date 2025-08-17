If you have .NET installed, you can build and run this:

```
# for a single file
dotnet run -c Release -- "path/to/foo.ytyp" --out "path/to/out_dir" --overwrite --lua

# for an entire folder (recursively)
dotnet run -c Release -- "path/to/folder" --out "path/to/out_dir" --overwrite --lua
```

If you have Visual Studio 2022 installed, I included a .sln for it too so you can just double-click it and select Build, Build Solution.
This will produce a binary folder in `bin\Release\net8.0\`.

In order to make this a little bit more accessible, I've included some CodeWalker.Core.dll and SharpDX.Mathematics.dll for which all rights are held by the CodeWalker development team.
The build supplied have assemblies from the r48 build of CodeWalker.
