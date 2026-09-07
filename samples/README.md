# samples/

Mounted read-only into the worker as `/sources` by default (see `ATLAS_LOCAL_SOURCES` in `.env`).
Drop a .NET repository here, or point `ATLAS_LOCAL_SOURCES` at a folder of your own, then create an
assessment with `"sourceKind": "local"` and `"sourceLocator": "/sources/<folder>"`.
