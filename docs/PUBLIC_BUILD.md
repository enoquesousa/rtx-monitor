# Build público e pacote privado local

O build padrão inclui a interface Rust e a telemetria pública C/C#/NVML. Não
inclui os leitores de pesquisa, suas provas, o manifesto ou o Python embarcado.
O coletor incluído não produz leituras privadas. A interface pode receber
leituras de um serviço local separado já ativo; ausência aparece indisponível,
sem substituir o valor por zero.

```powershell
cmake --preset windows-x64
.\scripts\build-desktop.ps1
.\scripts\package-desktop.ps1 -SkipBuild
```

Esses comandos usam `artifacts/desktop/dist-public` e
`artifacts/desktop/installer-public`. O gerador do MSI recusa um staging
público que contenha `research-readers` ou Python. A aplicação também recusa
um pacote privado parcial.

Um pacote privado local completo pode ser construído com os leitores revisados
presentes em `scripts/research-readers/`:

```powershell
cmake --preset windows-x64
.\scripts\build-desktop.ps1 -IncludePrivateReaders
.\scripts\package-desktop.ps1 -SkipBuild -IncludePrivateReaders
```

O modo privado usa `artifacts/desktop/dist-private` e
`artifacts/desktop/installer-private` e verifica todos os bytes do manifesto
antes e depois da publicação. Esses artefatos e o pacote de pesquisa são
locais; não os envie ao repositório público. O modo público continua exigindo
Windows x64 e as dependências de build descritas em [NATIVE_WINDOWS.md](NATIVE_WINDOWS.md).
