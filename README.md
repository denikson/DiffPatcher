# NetDiffPatcher

A proof-of-concept tool to create an assembly diff between two assemblies and patch it back into another assembly.

The tool is based on the relinker from [MonoMod](https://github.com/0x0ade/MonoMod) and is originally intended
for patching `System.Reflection.Emit` namespace in .NET Standard 2.0 `mscorlib.dll` bundled with Unity 20xx games.

## Description of projects

### CorlibDiff

The diff tool that takes two assemblies ("from" and "to" assemblies) and generates an assembly that contains the difference between both.

For now the project is set up to process `mscorlib.dll` by having `from_mscorlib.dll` and `to_mscorlib.dll`.  
Can be easily converted to work in generic cases.

### CorlibPatch

The main proof-of-concept patch tool intended to convert .NET Standard 2.0 `mscorlib.dll` into one 
that has `System.Reflection.Emit` properly implemented. For more info about `System.Reflection.Emit` and Unity games,
refer to [BepInEx/BepInEx#67](https://github.com/BepInEx/BepInEx/issues/67), [newman55/unity-mod-manager#20](https://github.com/newman55/unity-mod-manager/issues/20) 
and [pardeike/Harmony#172](https://github.com/pardeike/Harmony/issues/172).

To build the tool, generate the diff using `CorlibDiff` and put it into the project. 
Currently set up to only work with `mscorlib.dll`, but can be easily changed to work with any assembly.

To use the built tool, simply drag and drop .NET Standard 2.0 `mscorlib.dll` onto the executable. 
The tool will patch and generate a fixed DLL.

### NetDiffPatch

The main library that does diffing/patching.