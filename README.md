# Próximas descargas

API ASP.NET Core (.NET 10) com SQL Server, CRUD, filtros, importação/exportação `.xlsx` e integração HTTP com a StocksAPI.

## Estrutura

O projeto executável está em `APIEntradasSaidas/APIEntradasSaidas.csproj`. A pasta `src` está dentro de `APIEntradasSaidas` e contém `Controllers`, `Data`, `Dtos`, `Models`, `Services`, `Auth` e `Integrations`. O projeto inclui automaticamente os ficheiros C# dessa pasta. As migrations ficam em `APIEntradasSaidas/src/Data/Migrations`. O `Program.cs`, o `.csproj` e as configurações de execução ficam na raiz de `APIEntradasSaidas`. Existe um único `APIcontext`, partilhado pela aplicação e pelas ferramentas de migrations.

## Integração com a StocksAPI

Os contratos foram obtidos do código em `StocksAPI`:

- `GET /api/Cliente/{id}`: obtém `Id`, `Nome` e `IdEntidade`.
- `GET /api/Entidade/{id}`: obtém o nome da entidade.
- `GET /api/Cliente?pageNumber=...&pageSize=...`: lista clientes para seleção.
- A autenticação usa o cabeçalho `X-API-KEY`, configurado em `Authentication:ApiKey` na StocksAPI.

A nova API faz estas chamadas por `HttpClient`; não acede diretamente à BD da StocksAPI. O endereço base pode incluir um prefixo de alojamento. Redirecionamentos automáticos estão desativados; configura o endereço final HTTP/HTTPS da StocksAPI.

Cada `Cliente.Id` tem **uma única configuração de descarga**, garantida por um índice único na BD. `ID Cliente` no Excel corresponde a esse inteiro da StocksAPI; `001` e `1` representam o mesmo ID. Não usar o nome nem a referência como identificador.

O nome do cliente e a entidade são obtidos da StocksAPI no CRUD. A unidade usa inicialmente `Cliente.Nome`, podendo ser indicada no JSON. Na importação, a unidade de um registo existente é preservada; para novos registos usa-se `Cliente.Nome`. Esta é a convenção adotada porque o Excel fornecido não tem coluna de unidade. O nome apresentado no Excel pode ser atualizado na importação. O ficheiro mantém exatamente as seis colunas pedidas.

## Configurar e executar

1. Inicia a StocksAPI num terminal separado, com a sua configuração de BD e chave habitual:

```powershell
dotnet run --project C:\Users\spino\Desktop\StocksAPI\src\src.csproj --launch-profile http
```

2. Num terminal na raiz deste workspace, configura a nova API:

```powershell
# Chave para os consumidores chamarem ESTA API.
$env:Authentication__ApiKey = "SUBSTITUIR_PELA_CHAVE_DA_NOVA_API"

# Endereço e chave para ESTA API chamar a StocksAPI.
$env:StocksApi__BaseUrl = "http://localhost:5275/"
$env:StocksApi__ApiKey = "SUBSTITUIR_PELA_CHAVE_CONFIGURADA_NA_STOCKSAPI"

# A configuração incluída usa SQL Server LocalDB e uma BD independente.
# Se usares SQL Server/SQL Express, indica a ligação aqui:
# $env:ConnectionStrings__DefaultConnection = "Server=localhost\SQLEXPRESS;Database=APIEntradasSaidas;Trusted_Connection=True;TrustServerCertificate=True"

dotnet restore .\APIEntradasSaidas\APIEntradasSaidas.csproj
dotnet ef database update --project .\APIEntradasSaidas\APIEntradasSaidas.csproj
dotnet run --project .\APIEntradasSaidas\APIEntradasSaidas.csproj --launch-profile https
```

A migration já está criada; `database update` aplica-a à BD configurada. A aplicação não aplica migrations automaticamente. Os comandos EF podem ser executados na raiz ou na pasta do projeto. Para adicionar futuras migrations, usar `--output-dir src/Data/Migrations --namespace APIEntradasSaidas.Data.Migrations` (o caminho é relativo à pasta do projeto). A factory respeita `DOTNET_ENVIRONMENT`, `ASPNETCORE_ENVIRONMENT` e variáveis de ambiente.

Swagger: `https://localhost:7118/swagger`. Usa **Authorize** com a chave **da nova API**. Também há exemplos em `APIEntradasSaidas/APIEntradasSaidas.http`.

As chaves vazias em `appsettings.json` devem ser configuradas por ambiente; não guardar credenciais reais no código. Todas as rotas de negócio exigem autenticação. O Swagger só é disponibilizado em Development.

## Endpoints

| Método | Rota | Função |
| --- | --- | --- |
| GET | `/api/stocks/clientes` | Consultar clientes da StocksAPI (`pagina`, `tamanhoPagina`) |
| GET | `/api/stocks/clientes/exportar` | Gerar um modelo Excel com os IDs e nomes de todos os clientes da StocksAPI |
| GET | `/api/proximas-descargas` | Consultar configurações com filtros, ordenação e paginação |
| GET | `/api/proximas-descargas/{idCliente}` | Obter uma configuração |
| GET | `/api/proximas-descargas/{idCliente}/proxima` | Calcular a próxima ocorrência; `referencia=yyyy-MM-dd` opcional |
| POST | `/api/proximas-descargas` | Criar uma configuração; 409 se o cliente já tiver uma |
| PUT | `/api/proximas-descargas/{idCliente}` | Atualizar uma configuração existente |
| DELETE | `/api/proximas-descargas/{idCliente}` | Eliminar uma configuração |
| GET | `/api/proximas-descargas/exportar` | Descarregar todos os registos em Excel |
| POST | `/api/proximas-descargas/importar` | Importar Excel por `multipart/form-data`, campo `ficheiro` |

Filtros: `entidade`, `unidade`, `dataInicio`, `dataFim`, `periodoHorario`. As datas são inclusivas e incidem sobre a data base configurada, também devolvida no Excel. Ordenação: `ordenarPor=entidade|unidade|dataPrevista|periodoHorario|idCliente|nomeCliente` e `descendente=true|false`. Paginação: `pagina` (predefinida 1), `tamanhoPagina` (predefinido 50, máximo 500). O Excel exporta sempre a tabela inteira.

Exemplo de POST/PUT:

```json
{
  "idCliente": 1,
  "dataPrevista": "2026-10-20",
  "periodoHorario": "Manhã",
  "periodicidadeDias": 7,
  "observacoes": "Entregar na receção"
}
```

## Excel e importação

Para começar a partir dos clientes da StocksAPI, no Swagger usa **StocksClientes → GET /api/stocks/clientes/exportar → Try it out → Execute → Download file**. O ficheiro `clientes_stocks_proximas_descargas.xlsx` já traz `ID Cliente` e `Nome do Cliente`, obtidos de todas as páginas da StocksAPI (até 10 000 clientes). Preenche `Próxima Descarga`, `Período` e `Periodicidade (dias)`, acrescenta observações se necessário, e guarda como `.xlsx`. Depois envia o ficheiro em **POST /api/proximas-descargas/importar**. O download só consulta os clientes; é a importação que cria ou atualiza os registos na BD. Este modelo deixa os campos de agendamento vazios mesmo para clientes com configurações locais; para editar as configurações já guardadas, usa **GET /api/proximas-descargas/exportar**.

Colunas exatas, pela mesma ordem:

`ID Cliente | Nome do Cliente | Próxima Descarga | Período | Periodicidade (dias) | Observações`

- Uma única folha de dados, `.xlsx`, até 5 MB, até 10 000 linhas de dados.
- Cabeçalhos obrigatórios; linhas completamente vazias são ignoradas.
- IDs inteiros positivos, sem duplicados no ficheiro, existentes na StocksAPI.
- Nome obrigatório, até 255 caracteres; observações opcionais, até 2000.
- Datas Excel ou texto `dd/MM/yyyy` / `yyyy-MM-dd`, a partir de 1900-01-01, sem hora.
- Período `Manhã` ou `Tarde`, periodicidade inteira maior que zero; fórmulas são rejeitadas.
- Toda a validação do ficheiro e dos clientes acontece antes de alterar a BD.
- Os IDs existentes são atualizados; os restantes são criados. Registos ausentes do ficheiro não são eliminados.
- Um único `SaveChangesAsync` grava a importação numa transação. Erros devolvem HTTP 400 com `erros: [{ linha, mensagem }]`; linha 0 identifica erros do ficheiro/cabeçalhos. Conflitos concorrentes de ID devolvem 409.

O cálculo de recorrência usa a data base e a periodicidade, devolvendo a primeira ocorrência igual ou posterior à referência, sem alterar a configuração. A referência predefinida é a data atual em `Europe/Lisbon`; inclui o próprio dia porque só existe período manhã/tarde, sem hora exata. Se não existir uma ocorrência representável até ao ano 9999, devolve `proximaData: null`. O cálculo é feito quando se consulta o endpoint; não existe tarefa agendada a alterar datas.

## Validação e âmbito

```powershell
dotnet test .\APIEntradasSaidas.Tests\APIEntradasSaidas.Tests.csproj
```

Os testes exercitam os endpoints reais com SQLite em memória e respostas HTTP simuladas da StocksAPI, incluindo o cabeçalho de autenticação, as rotas, o CRUD, os filtros, a recorrência e a importação sem alterações parciais. A migration de produção destina-se a SQL Server. Uma chamada à StocksAPI real depende de ela estar em execução e das credenciais/configuração locais.

O código do BO GesTools não está neste workspace; os endpoints de download/upload estão prontos para essa interface consumir. A geração do pedido formal de alteração e o acompanhamento em tempo real exigem os respetivos contratos e não estão implementados aqui.

A compilação sinaliza avisos de vulnerabilidades NuGet em dependências (`Microsoft.OpenApi`, `System.Security.Cryptography.Pkcs` e a biblioteca SQLite dos testes). A atualização dessas dependências continua pendente.

Referências: [configuração de DbContext](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/), [clientes HTTP tipados](https://learn.microsoft.com/en-us/dotnet/core/extensions/http-client), [ClosedXML](https://docs.closedxml.io/en/latest/).
