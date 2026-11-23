# Third-party notices / Drittanbieter-Hinweise

Gimbo Konverter itself is licensed under the [MIT License](LICENSE). The release download also contains
the third-party software listed below, each under its own license.

Gimbo Konverter selbst steht unter der [MIT-Lizenz](LICENSE). Der Download im Release enthält zusätzlich
die folgende Software anderer Hersteller, jeweils unter deren eigener Lizenz.

---

## FFmpeg

- **Included / Enthalten:** `ffmpeg.exe` and `ffprobe.exe`, unmodified Windows builds by Gyan Doshi
  ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/)), embedded in `Gimbo Konverter.exe` and unpacked to
  `%LOCALAPPDATA%\GimboKonverter\ffmpeg` on first use. The exact version is shown in the app on the ffmpeg page.
- **License / Lizenz:** GNU General Public License, version 3 or later. Full text: [licenses/GPL-3.0.txt](licenses/GPL-3.0.txt)
  (also included in the release ZIP as `FFmpeg-GPL-3.0.txt`).
- **Copyright:** © the FFmpeg developers, see [ffmpeg.org](https://ffmpeg.org/). FFmpeg is a trademark of Fabrice Bellard.
- **Source code / Quellcode:** FFmpeg source releases are available at [ffmpeg.org/download.html](https://ffmpeg.org/download.html),
  build information and the sources of the libraries contained in the builds at
  [gyan.dev/ffmpeg/builds](https://www.gyan.dev/ffmpeg/builds/). On request, the author of Gimbo Konverter provides the
  corresponding source code of the included FFmpeg version for at least three years after the release.
- Gimbo Konverter runs FFmpeg as a separate program and does not link against it.
  Gimbo Konverter startet FFmpeg als eigenständiges Programm und bindet es nicht als Bibliothek ein.
- The optional in-app ffmpeg update downloads the same builds directly from
  [GyanD/codexffmpeg](https://github.com/GyanD/codexffmpeg) (GitHub) or gyan.dev.
- Some codecs (for example H.264, HEVC) may be covered by patents in some countries. See
  [ffmpeg.org/legal.html](https://ffmpeg.org/legal.html).

## WPF-UI

- **Version:** 3.0.5 · [github.com/lepoco/wpfui](https://github.com/lepoco/wpfui)
- **License / Lizenz:** MIT · Copyright (C) 2021-2024 Leszek Pomianowski and WPF UI Contributors

## CommunityToolkit.Mvvm

- **Version:** 8.2.2 · [github.com/CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet)
- **License / Lizenz:** MIT · (c) .NET Foundation and Contributors. All rights reserved.
- Further notices of components contained in the package:
  [CommunityToolkit/dotnet ThirdPartyNotices.txt](https://github.com/CommunityToolkit/dotnet/blob/main/ThirdPartyNotices.txt)

## .NET Runtime, WPF and Windows Forms

- The release is published self-contained and includes the .NET 8 runtime.
- **License / Lizenz:** MIT · Copyright (c) .NET Foundation and Contributors ·
  [github.com/dotnet/runtime](https://github.com/dotnet/runtime), [github.com/dotnet/wpf](https://github.com/dotnet/wpf),
  [github.com/dotnet/winforms](https://github.com/dotnet/winforms)
- Further notices of components contained in .NET:
  [dotnet/runtime THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)

---

## MIT License (text for the components marked MIT above)

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Trademarks / Marken

NVIDIA, AMD, Intel, Apple, Windows and other names are trademarks of their respective owners. Their use here
only describes compatibility and does not imply any endorsement.

NVIDIA, AMD, Intel, Apple, Windows und andere Namen sind Marken ihrer jeweiligen Inhaber. Sie werden hier nur
zur Beschreibung der Kompatibilität genannt, eine Verbindung oder Empfehlung ist damit nicht verbunden.
