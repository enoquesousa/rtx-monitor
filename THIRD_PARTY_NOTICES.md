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

The public runtime declares the minimal NVML ABI needed for interoperability;
it does not distribute the NVIDIA NVML binary, import library, or driver. The
separate, locally held research archive includes the NVIDIA header at
`scripts/research-readers/vendor/validation-2026-10-24/public-reference/nvml_official.h`.
Its original notice is reproduced below. Consult the header and its source
distribution for the applicable terms; the project's MIT license does not
replace third-party terms. The authoritative API contract is the
[official NVML documentation](https://docs.nvidia.com/deploy/nvml-api/).

> Copyright 1993-2026 NVIDIA Corporation. All rights reserved.
>
> NOTICE TO USER:
>
> This source code is subject to NVIDIA ownership rights under U.S. and
> international Copyright laws. Users and possessors of this source code
> are hereby granted a nonexclusive, royalty-free license to use this code
> in individual and commercial software.
>
> NVIDIA MAKES NO REPRESENTATION ABOUT THE SUITABILITY OF THIS SOURCE
> CODE FOR ANY PURPOSE. IT IS PROVIDED "AS IS" WITHOUT EXPRESS OR
> IMPLIED WARRANTY OF ANY KIND. NVIDIA DISCLAIMS ALL WARRANTIES WITH
> REGARD TO THIS SOURCE CODE, INCLUDING ALL IMPLIED WARRANTIES OF
> MERCHANTABILITY, NONINFRINGEMENT, AND FITNESS FOR A PARTICULAR PURPOSE.
> IN NO EVENT SHALL NVIDIA BE LIABLE FOR ANY SPECIAL, INDIRECT, INCIDENTAL,
> OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
> OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE
> OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE
> OR PERFORMANCE OF THIS SOURCE CODE.
>
> U.S. Government End Users. This source code is a "commercial item" as
> that term is defined at 48 C.F.R. 2.101 (OCT 1995), consisting of
> "commercial computer software" and "commercial computer software
> documentation" as such terms are used in 48 C.F.R. 12.212 (SEPT 1995)
> and is provided to the U.S. Government only as a commercial end item.
> Consistent with 48 C.F.R. 12.212 and 48 C.F.R. 227.7202-1 through
> 227.7202-4 (JUNE 1995), all U.S. Government End Users acquire the
> source code with only those rights set forth herein.
>
> Any use of this source code in individual and commercial software must
> include, in the user documentation and internal comments to the code,
> the above Disclaimer and U.S. Government End Users Notice.

## Microsoft.Data.Sqlite

The `RtxMonitor.Storage` project uses [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite), distributed under the MIT License. The package is used only by the optional C# persistence layer and is not linked into `rtxmon_native`.

## Windows SDK research references

The separate, locally held research archive includes headers from the Windows SDK in
`scripts/research-readers/vendor/_external/windows-sdk-10.0.26100.0/`. These
are Microsoft materials and are not covered by the project's MIT license.
These files are excluded from the public repository and public build.

The local archive also contains output from static analysis of the NVIDIA
driver. It is excluded from the public repository and public build.

## SQLitePCLRaw

`Microsoft.Data.Sqlite` brings the [SQLitePCLRaw](https://www.nuget.org/packages/SQLitePCLRaw.bundle_e_sqlite3) packages as transitive dependencies. SQLitePCLRaw is distributed under the Apache License 2.0.

## SQLite

The bundled native SQLite library is part of the [SQLite project](https://www.sqlite.org/copyright.html). SQLite's deliverable source and documentation have been dedicated to the public domain by their authors.

## Microsoft.Extensions.Hosting.WindowsServices

`RtxMonitor.Service` uses [Microsoft.Extensions.Hosting.WindowsServices](https://www.nuget.org/packages/Microsoft.Extensions.Hosting.WindowsServices/8.0.1) to integrate the .NET host with the Windows Service Control Manager. The package is distributed under the MIT License.
