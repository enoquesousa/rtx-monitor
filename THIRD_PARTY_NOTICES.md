# Third-party notices

## NVIDIA NVAPI SDK

The minimal NVAPI ABI declarations and public interface identifiers used by this project are derived from the [NVIDIA NVAPI SDK](https://github.com/NVIDIA/nvapi). The NVAPI implementation itself is supplied by the installed NVIDIA driver and is not distributed by this repository.

Copyright (c) 2024 NVIDIA CORPORATION & AFFILIATES. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## NVIDIA Open GPU Kernel Modules — RM thermal protocol

The minimal transport-independent RM thermal command identifiers, opcodes, and ABI layouts in `rm_thermal_protocol.hpp` are derived from the NVIDIA Open GPU Kernel Modules header `ctrl2080thermal.h`. The project does not redistribute or load the NVIDIA kernel module and does not claim that the Linux transport is available on Windows.

Copyright (c) 2005-2022 NVIDIA CORPORATION & AFFILIATES. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## NVIDIA Management Library

This project declares only the minimal public NVML ABI needed for runtime interoperability. It does not redistribute NVIDIA's NVML header, import library, binary, or driver. The authoritative API contract is the [official NVML documentation](https://docs.nvidia.com/deploy/nvml-api/).

## Microsoft.Data.Sqlite

The `RtxMonitor.Storage` project uses [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite), distributed under the MIT License. The package is used only by the optional C# persistence layer and is not linked into `rtxmon_native`.

## SQLitePCLRaw

`Microsoft.Data.Sqlite` brings the [SQLitePCLRaw](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3) packages as transitive dependencies. SQLitePCLRaw is distributed under the Apache License 2.0.

## SQLite

The bundled native SQLite library is part of the [SQLite project](https://www.sqlite.org/copyright.html). SQLite's deliverable source and documentation have been dedicated to the public domain by their authors.

## Microsoft.Extensions.Hosting.WindowsServices

`RtxMonitor.Service` uses [Microsoft.Extensions.Hosting.WindowsServices](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.WindowsServices/8.0.1) to integrate the .NET host with the Windows Service Control Manager. The package is distributed under the MIT License.
