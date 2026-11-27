# ADR 0001 — Runtime suportado desde o primeiro deploy

O roteiro parte do .NET 8 para estudo e prevê migrar antes do deploy. Como o repositório é novo e a data de fim de suporte daquela linha já passou, não há legado a preservar. A entrega começa em **.NET 10 LTS**. Não se cria uma migração histórica fictícia para preencher o Git.

O `global.json` e as imagens Docker tornam a escolha verificável. Ver a [política oficial da Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core).
