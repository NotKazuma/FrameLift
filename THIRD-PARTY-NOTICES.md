# Third-party notices

FrameLift is MIT licensed (see [LICENSE](LICENSE)). It depends on the following third-party software,
unmodified. Nothing below is vendored in this repository.

## LibreHardwareMonitorLib
- License: MPL-2.0
- Source: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Used as: a NuGet package reference (`LibreHardwareMonitorLib`, v0.9.6) to read CPU and GPU temperatures.
  Its source is not redistributed as part of this repository.

## PawnIO
- License: see https://github.com/namazso/PawnIO
- Not bundled. LibreHardwareMonitorLib can use it at runtime, as a driver the user installs separately
  (for example `winget install namazso.PawnIO`), to read CPU temperature sensors.

## .NET and System.ServiceProcess.ServiceController
- License: MIT
- Source: https://github.com/dotnet/runtime
- Used as: the .NET 8 runtime (the published exe is self-contained) and a NuGet package for starting and stopping Windows services.

## nvidia-smi
- Shipped with the NVIDIA driver; not bundled and not redistributed. FrameLift calls it, when present, to read
  the GPU temperature and to set the GPU power limit.
