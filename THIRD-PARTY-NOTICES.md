# Third-party notices

UwUTerm is MIT licensed; see [LICENSE](LICENSE). It also carries code derived from the
projects below, whose licences are reproduced here in full. Any source file containing
ported code names its origin in a header comment, and the licence for that origin is
this file.

## XtermSharp

- Upstream: <https://github.com/migueldeicaza/XtermSharp>
- Revision read: `1bed529cd20748ef9da43753e8dd553f65fc5582`
- Licence: MIT

XtermSharp is a .NET port of xterm.js, so its licence carries the copyright of both, and
all four notices below travel together.

UwUTerm does not link against it. Grey Hack's shell runs on the server and sends back
formatted lines rather than a byte stream, so there is nothing for a VT parser to consume
and the library cannot be used as one. What is taken is the design and implementation of
the screen model - the packed cell, the buffer line, the scrollback and the reflow
strategies - ported by hand onto Unity's types.

```
Copyright (c) 2017-2019, The xterm.js authors (https://github.com/xtermjs/xterm.js)
Copyright (c) 2014-2016, SourceLair Private Company (https://www.sourcelair.com)
Copyright (c) 2012-2013, Christopher Jeffrey (https://github.com/chjj/)
Copyright (c) 2019 Miguel de Icaza (https://github.com/migueldeicaza)

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```
