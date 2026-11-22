// Fixture for tools/agreement/node/measure.test.mjs (spec 015, US7.2): a cut-down stand-in for the
// _framework/dotnet.js that `dotnet publish src/MmoEngine.SimCore.Wasm -c Release` writes. .NET 10 embeds
// the boot manifest in the loader as the argument of withConfig, between two comment markers, so a
// measurement reads the downloaded files from here (contracts/measurement-report.md, "Download size").
// The manifest keeps the published shape (debugLevel 0, invariant globalization, the resource groups
// and their entries' fields) with the eight file names of the contract's example, and no icu group.
// The hashes are placeholders. Only the text is read; nothing imports this file.
const loader = {
    config: null,
    withConfig(config) {
        this.config = config;
        return this;
    },
};
const dotnet = loader.withConfig(/*json-start*/{
  "mainAssemblyName": "MmoEngine.SimCore.Wasm",
  "resources": {
    "hash": "sha256-UMoYyxoGILiDHck3s2UQE16cdLYFdyVXyOrs0kWfdEY=",
    "jsModuleNative": [
      {
        "name": "dotnet.native.kakcbpgnoi.js"
      }
    ],
    "jsModuleRuntime": [
      {
        "name": "dotnet.runtime.v2rwe8u38b.js"
      }
    ],
    "wasmNative": [
      {
        "name": "dotnet.native.fdp95x8kng.wasm",
        "hash": "sha256-oOH8xTG0E2e5K5dRfjzsyaabZe2P5HO9Bu6WBd+5hTk=",
        "cache": "force-cache"
      }
    ],
    "coreAssembly": [
      {
        "virtualPath": "System.Private.CoreLib.wasm",
        "name": "System.Private.CoreLib.2kv5b1q4gv.wasm",
        "hash": "sha256-d1ZOPBrQ25wicnbp9ApoDvqOtmVCLYngWEOGRzYN9Jc=",
        "cache": "force-cache"
      },
      {
        "virtualPath": "System.Runtime.InteropServices.JavaScript.wasm",
        "name": "System.Runtime.InteropServices.JavaScript.r8sucgoam9.wasm",
        "hash": "sha256-+g4oOREwGQU1tbkTd1cEipFGLbpa6ipqjlQX6V2dVHM=",
        "cache": "force-cache"
      }
    ],
    "assembly": [
      {
        "virtualPath": "System.IO.Hashing.wasm",
        "name": "System.IO.Hashing.x8z6mup4sz.wasm",
        "hash": "sha256-3EZJJAT+4sfA6/1NSzqwFI7p0KzY1WpuM04RptEK5FI=",
        "cache": "force-cache"
      },
      {
        "virtualPath": "MmoEngine.SimCore.wasm",
        "name": "MmoEngine.SimCore.a0eeztrxtm.wasm",
        "hash": "sha256-yP0NiSSZB33WJuqUC45UBiI74asmqywmTruB+os4e2U=",
        "cache": "force-cache"
      },
      {
        "virtualPath": "MmoEngine.SimCore.Wasm.wasm",
        "name": "MmoEngine.SimCore.Wasm.mjw9736tm5.wasm",
        "hash": "sha256-6P9Kdg7j8Gg4QeRuWzGYPIqp8JYFT+EHmIZWCBTe6Yc=",
        "cache": "force-cache"
      }
    ]
  },
  "debugLevel": 0,
  "linkerEnabled": true,
  "globalizationMode": "invariant",
  "runtimeConfig": {
    "runtimeOptions": {
      "configProperties": {
        "System.Globalization.Invariant": true,
        "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported": true
      }
    }
  }
}/*json-end*/);
export { dotnet as default, dotnet };
