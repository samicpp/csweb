# csweb
a highly configurable webserver that uses [samicpp/dotnet-http](https://github.com/samicpp/dotnet-http) <br/>


## Config

### Server settings
inside `appsettings.json` you can specify the work and serve directory, which ports to listen to, ssl certificate, and log level. <br/>
this file needs to be in the same directory as the application or project directory. <br/>
see [appsettings.default.json](./web/appsettings.default.json) for the default (or fallback if not present)
and [appsettings.comments.json](./web/appsettings.comments.json) for comments explaining each item. <br/>
appsettings.default.json
```json
{
    "09-address": [ ],
    "h2c-address": [ ],
    "h2-address": [ ],
    "ssl-address": [ ],
    "poly-address": [ ],
    
    "p12-cert": null,
    "p12-pass": null,
    "alpn": [ "h2", "http/1.1" ],
    "fallback-alpn": null,
    
    "cwd": null,
    "backlog": 10,
    "dualmode": false,

    "serve-dir": "./",
    "use-compression": true,
    "bigfile-threshold": 16777216,
    "bigfile-chunk-size": 16777216,
    "stream-bigfiles": false,

    "loglevel": null
}
```

### Destination settings
destination settings are inside of `routes.json` which need be located in the serve directory. <br/>
this file should contain a syntex similar to the following:
```json
{
    "default": {
        "dir": "sub dir"
    },

    "query": {
        "match-type": "type",
        "dir": "sub dir"
    },

    "query2": {
        "match-type": "type",
        "dir": "sub dir",
        "router": "file"
    }
}
```
- `match-type` string = "host" | "start" | "end" | "regex" | "path-start" | "scheme" | "protocol" | "domain"
- - `host`: matches the host from the host header provided by the client
- - `start`: matches the start of the whole requested uri (`scheme://host/path`)
- - `end`: matches the end of the whole requested uri
- - `path-start`: matches only the start of the requested path
- - `scheme`: matches the scheme, always `http` or `https` unless using a non official version
- - `protocol`: matches the protocol used (http version), always `HTTP/1.1` | `HTTP/2` (| `HTTP/3` planned)
- - `domain`: matches the domain (e.g. `www.example.com` matches domain `example.com`)
- `dir` is used to decide the requested file `CWD/ServeDir/SubDir/Path`
- `router` specifies a file to be used instead of path to calculate destination. always a file

the server will attempt to match each item starting at the top (skipping over `default`) and will fetch content from the sub directory starting at serve directory. <br/>
if this file isnt present it will attempt to fetch content from the serve directory directly. <br/>
if nothing was matched it will try to use the `default` entry. required to be present <br/>
when the file is changed the server will refresh its configuration. if the file is removed it wont remove the config <br/>
matching is case insensitive <br/>

### Static Headers
in the same location where the destination settings are located, you can also optionally include a `headers.json` file. <br/>
the server will set these headers regardless of the request.
```json
{
    "Header1": "value1",
    "Header2": "value2"
}
```

## Files
besides normal files the server also supports some files that invoke custom behaviour <br/>
| file extension | description |
|--|--|
| `*.3xx.redirect` | redirects to the contents of the file. is a "special file" |
| `*.var.*` | contents get served with the correct content-type. is a "special file" |
| `*.link` | acts as a sort of symlink, needs to contain a absolute path or relative path to the cwd. is a "special file" |
| `*.dll` | loads that dll, hands it over control, and awaits it. dlls only get reimported if they change |
| `*.s.cs` \| `*.s.fs` \| `*.s.ps1` | **not yet supported**. hands over control to these sippets |
| `*.ffi.dll` \| `*.ffi.so` | **not yet supported**. loads a native library, passes it function pointers for "basic" http response and then invokes another function. |

### Special files
these files get read as pieces of text and then replaces certain strings with different strings <br/>
| variable | description |
|--|--|
| `"%IP%"` | gets replaced by the clients remote address |
| `"%FULL_IP%"` | gets replaced by the clients remote address and port |
| `%PATH%` | gets replaced by the path |
| `%HOST%` | gets replaced by the host |
| `%SCHEME%` | gets replaced by `http` or `https` |
| `%BASE_DIR%` | gets replaced by the serve directory |
| `%USER_AGENT%` | gets replaced by the full user agent header |
| `%DOMAIN%` | gets replaced by an estimated guess of the domain (`www.example.com` -> `example.com`) |


## Planned

### Features
- [x] allow serving files
- [x] allow simple dynamic content files
- [x] add middleware support
- [x] add protocol detection (auto choose HTTP/1.1, HTTP/2, Tls)
- [ ] add script support
- [ ] support partial content / byte ranges
- [ ] support simple builtin middleware to avoid duplication
- [ ] allow speicifying http authentication in routes config
- [ ] overhaul logging & allowing logserver and reports

### Enhancements
- [X] support tls
- [X] support json config files
- [x] add file caching
- [x] add regex file matching config files
- [ ] make cache use compressed data & allow pre compressed files
- [ ] update/modernize request handling logic

### Planning
| Version | Change | Date | Status |
|--|--|--|--|
| v1.x.x.x | Fundamental (HTTP/1.1) | 23-10-2025 | ✓ |
| v1.1.x.x | Support config | 23-10-2025 | ✓ |
| v2.x.x.x | HTTP/2 | 24-10-2025 | ✓ |
| v2.3.x.x | Support TLS | 24-10-2025 | ✓ |
| v2.8.x.x | Protocol detection | 02-01-2026 | ✓ |
| v2.11.x.x | Script support | - | - |
| v3.x.x.x | HTTP/3 | - | - |
| v4.x.x.x | Switch to self made web framework | - | - |
| v5.x.x.x | Build parts in C++ | - | - |

## Versioning scheme
- `Y.x.x.x`: fundamental/major change
- `x.Y.x.x`: added features / big changes
- `x.x.Y.x`: minor features / big bugfix
- `x.x.x.Y`: very small changes / smaller bugfix
