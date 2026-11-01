# Third-party code, data and licences

## Dev-Jahn/poe2-ritual-checker

Source: https://github.com/Dev-Jahn/poe2-ritual-checker, commit 7c16291. Taken over and adapted:

- `src/Poe2Pricer.Core/Recognizer.cs` — from `src/Ritual.Core/Vision.cs`: building the
  references from the catalogue pictures, the occupied-cell test, template comparison (grey +
  edges), colour penalty, choice of candidates.
- `src/Poe2Pricer.Core/RegionPartitioner.cs` — from `src/Ritual.Core/RegionPartitioner.cs`,
  unchanged but for the namespace.
- `src/Poe2Pricer.App/OverlayWindow.cs` — the overlay's window styles (click-through, no focus,
  `SetWindowDisplayAffinity(0x11)`) after `src/Ritual.App/Overlay.cs`.
- `src/Poe2Pricer.Core/GameCapture.cs` — finding the game by its process name and the BitBlt
  capture of the client area after `src/Ritual.Core/WindowsCapture.cs`.
- `src/Poe2Pricer.Core/Market/NinjaClient.cs` — poe.ninja endpoints and category list after
  `src/Ritual.Core/NinjaMarket.cs`; the colour tiers in divine after `Presentation.ValuePosition`.

Not taken over: grid search, tooltip/OCR evaluation, Korean interface, controller cursor mask,
trade queries, screen recording.

`data/catalog/recognition-model.json` comes from Dev-Jahn's project (MIT): the appearance of
the items learned from his game pictures, stamped with the version of the catalogue it is
shipped with. The catalogue itself (`catalog.json`, `images/`) was taken from his data folder
up to version 1.4.0; since then poe2pricer builds it from the sources named under "Item
catalogue" below. The shape of `catalog.json` and the item ids are still his.

```
MIT License

Copyright (c) 2026 Dev-Jahn

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

This license applies to original project code. Game artwork, item text and
third-party dependencies retain their respective rights and licenses.
```

## Font Caveat Bold 2.000 (The Caveat Project Authors)

`data/fonts/Caveat-Bold.ttf`, built into `Poe2Pricer.Core` unchanged (303,088 bytes), from
https://github.com/googlefonts/caveat (`fonts/ttf/Caveat-Bold.ttf`). The German client of Path of
Exile 2 writes the rumour lines of the panel in this font (it carries the older version 1.500,
`art/2dart/fonts/caveat-bold.ttf`, which the project no longer publishes; against the game's lines
both versions measure the same). poe2pricer draws the German rumour lines with it to recognise
them by their picture (`RumourLineArt.cs`).

Licence (`data/fonts/Caveat-OFL.txt`):

```
Copyright 2014 The Caveat Project Authors (https://github.com/googlefonts/caveat)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL


-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded, 
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## Font Quikhand 1.00 (Joanne Taylor) — not included

poe2pricer recognises the English rumour lines of the "Island Rumours" panel by their picture: the
20 texts of the game, drawn in the game's font (`RumourLineArt.cs`). That font is Quikhand,
"Version 1.00 May 29, 2010, initial release" (25,668 bytes; Path of Exile 2 carries the same file
as `art/2dart/fonts/quikhand.ttf`), available from https://www.1001fonts.com/quikhand-font.html.
It is freeware; its terms allow use but not redistribution ("may not be sold or published",
1001fonts). **poe2pricer therefore does not contain the font** — neither in the program nor in
the repository. What ships instead are pictures of the 20 texts at five sizes
(`data/rumours/en-lines.json`, 100 line pictures as black-and-white masks), drawn once with the
font on the developer's machine (`Poe2Pricer.Cli rumour-lines`). The later version 1.10 is set
differently and does not match.

Terms according to the file that comes with the font:

```
The font file in this archive was created by Joanne Taylor in Knysna, South Africa, using Font Creator 5.6.

By using or installing this font data, you implicitly agree to certain conditions:

It can be used for any commercial or personal work.
It must not be altered or modified without my consent.
It may not be sold.
I cannot be held liable for any damage arising out of the use of this font.
```

dafont.com lists the font as "100% Free" with the author's note: "free for anyone to use
anywhere at any time in any way they like, no strings attached".

## Item catalogue (`data/catalog`, game material)

`catalog.json` names every item the Ritual grid can show, with the slot it takes in cells and
its picture; `images/` holds the pictures. `tools/catalog_update.py` builds both. The names,
slot sizes and art paths come from Grinding Gear Games: the item lists of poe.ninja (unofficial
use of its API; everything it prices) and the static item list of the trade site
(https://www.pathofexile.com/api/trade2/data/static; the currency-like items, priced or not).
The pictures of currency-like items come from the game's image service at
https://web.poecdn.com (64 px a cell); the pictures of uniques from https://cdn.poe2db.tw, by
the art path the image service names (108 px a cell: the image service renders uniques at
47 px a cell, too coarse for a one-cell unique — a charm read at .88 dropped to .40). The names
and pictures are material of Grinding Gear Games and are **not** covered by the MIT licence of
this program; 1219 entries and 1086 pictures, 42 MB, as of 2026-10-30.

## Game interface pieces (`data/ui`, game material)

The small pictures in `data/ui` are pieces of the interface of Path of Exile 2, used as
templates to find windows, tooltips, buttons and stash tabs in a capture: cut from the game's
interface files or from screenshots of the game. `data/ui/glyphs` holds 15 such pictures of
the lettering of item tooltips, the digits 0 to 9, the brackets, the minus, plus and per cent
signs, cut from screenshots at popup size 1.0; poe2pricer compares them with a tooltip where
the text recognition left a number out. All of these are material of Grinding Gear Games. The
tooltip lettering is set in the typeface Fontin SmallCaps by Jos Buivenga (exljbris,
https://www.exljbris.com), which is **not** included: poe2pricer ships no font file of it and
draws no text with it, only these cut-outs of the game's own rendering.

## Rumour texts (game data)

The 20 texts of the Island Rumours ("All that glitters...", "Somethin' fishy...", …) and the names
of their islands are built into poe2pricer (`RumourTexts.cs`), so that nothing is fetched from
third-party servers at run time. They come from the game tables of Path of Exile 2 (the table of
endgame maps; cross-checked with the public export https://github.com/repoe-fork/dat-export). The
texts are material of Grinding Gear Games.

## Velopack 1.2.161 (Velopack Ltd., Caelan Sayler)

Source: https://github.com/velopack/velopack (NuGet package `Velopack`; the `vpk` tool packs the
release). Licence: MIT. Used for the setup, the uninstall and the self-update of an installed copy
(`Updates.cs`); shipped as `Velopack.dll` and, in the installed copy, as `Update.exe` beside the
program folder.

```
Copyright © 2021 Caelan Sayler
Copyright © 2024 Velopack Ltd.

Permission is hereby granted,  free of charge,  to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to  use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

## CommunityToolkit.Mvvm 8.4.2 (.NET Foundation and Contributors)

Source: https://github.com/CommunityToolkit/dotnet (NuGet package `CommunityToolkit.Mvvm`,
published by Microsoft). Used in `src/Poe2Pricer.Ui` for the view models of the program window
(change notification, commands); shipped as `CommunityToolkit.Mvvm.dll`.

```
MIT License (MIT)

Copyright © .NET Foundation and Contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the “Software”), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or
substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## OpenCvSharp 4.11.0.20250507 (shimat) and OpenCV 4.11.0

Source: https://github.com/shimat/opencvsharp (NuGet packages `OpenCvSharp4` and
`OpenCvSharp4.runtime.win`, publisher shimat). The .NET binding of OpenCV; template matching,
greyscale, resizing and every other image computation of the recognition run on it
(`src/Poe2Pricer.Core`). Shipped are `OpenCvSharp.dll` and, under `runtimes/win-x64/native/`, the
OpenCV library itself as `OpenCvSharpExtern.dll` (OpenCV 4.11.0, https://github.com/opencv/opencv,
with OpenCvSharp's C interface in one file).

Licence of OpenCvSharp (Copyright 2008 shimat) and of OpenCV: Apache License 2.0, text below.

**FFmpeg wrapper:** `opencv_videoio_ffmpeg4110_64.dll` is OpenCV's wrapper around FFmpeg and,
by OpenCV's own statement (`3rdparty/ffmpeg/readme.txt`), under the LGPL, not Apache 2.0. OpenCV
loads it at run time and only for video files; poe2pricer opens no videos (no `VideoCapture` or
`VideoWriter` in the source). OpenCV says: whoever does not want to ship LGPL software leaves the
file out, and OpenCV stays fully functional except for video coding. **poe2pricer does not ship
the file:** the project `Poe2Pricer.App` removes it from the output folder after every build.

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```
