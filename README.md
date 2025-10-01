#  ![Logo](images/AILogo_LNUnit_small.png) LNUnit
--- 


LNUnit is a unit-testing framework for Bitcoin Lightning network systems. It provides an easy-to-use interface for
developers to write tests that check the functionality and performance of their Lightning network applications.

[![Build & Tested](https://github.com/nbd-wtf/LNUnit/actions/workflows/dotnet.yml/badge.svg)](https://github.com/nbd-wtf/LNUnit/actions/workflows/dotnet.yml)

[![Deploy Nuget Package](https://github.com/nbd-wtf/LNUnit/actions/workflows/nuget.yml/badge.svg)](https://github.com/nbd-wtf/LNUnit/actions/workflows/nuget.yml)


| Package   | Version                                                                                                                                     |
|-----------|---------------------------------------------------------------------------------------------------------------------------------------------|
| `LNUnit.LND` | [![NuGet version (LNUnit.LND)](https://img.shields.io/nuget/v/LNUnit.LND.svg?style=flat-square)](https://www.nuget.org/packages/LNUnit.LND) |
| `LNUnit` |   [![NuGet version (LNUnit)](https://img.shields.io/nuget/v/LNUnit.svg?style=flat-square)](https://www.nuget.org/packages/LNUnit)           |



## Features
---

- Support for LND gRPC
- Test cases can be written in C#
- Automated test discovery
- Test fixtures for setting up and tearing down test environments
- Integration with NUnit for continuous integration and coverage reporting
- Channel Acceptor
- HTLC Interception
- MIT License


## Notes
---

#### MacOS Users

To run the containerized tests we need to connect directly to the docker containers, but if you're using MacOS you won't be able to, thanks to the way Docker for Mac is implemented.

We're using [Docker Mac Net Connect](https://github.com/chipmk/docker-mac-net-connect) due to it's simplicity. Just run:

```sh
# Install via Homebrew
$ brew install chipmk/tap/docker-mac-net-connect

# Run the service and register it to launch at boot
$ sudo brew services start chipmk/tap/docker-mac-net-connect
```

Recently also have had good success with [OrbStack](https://orbstack.dev/), just works out of the box.