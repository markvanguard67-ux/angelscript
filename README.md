<p align="center"><img src="https://repository-images.githubusercontent.com/1036687907/6cd0e5bb-3856-4ad1-ac4c-da02be8e3ed7" alt="angelscript logo" width="50%" height="50%"></p>

The AngelCode Scripting Library, or AngelScript as it is also known, is an extremely flexible cross-platform scripting library designed to allow applications to extend their functionality through external scripts. It has been designed from the beginning to be an easy to use component, both for the application programmer and the script writer.

The official repository for AngelScript is: https://github.com/anjo76/angelscript

Please go to https://www.angelcode.com/angelscript for more information.

## Experimental C# / Unity Burst implementation

This fork also contains an **unofficial C# implementation of a primitive AngelScript subset** in [`csharp/`](csharp/README.md), with an unmanaged bytecode VM and Unity parallel-job adapter. It is not a complete port or the upstream AngelCode library; the original C++ SDK is unchanged. See the [C# guide](csharp/README.md) for supported features, installation, tests, and the current Unity/Burst verification limits.

For a runnable Unity test scene, native-oracle fixtures, Burst/job checks and the full-port gap ledger, see [the validation laboratory](csharp/UnityValidation/README.md).
