using System.Text.Json;
using APIEntradasSaidas.Dtos;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace APIEntradasSaidas.Services;

/// <summary>
/// Centraliza a conversão de exceções em respostas HTTP com um formato de erro comum.
/// É registado no Program.cs e executado pelo middleware UseExceptionHandler.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// Tenta responder à exceção. Devolve true quando escreveu a resposta e false
    /// quando deixa o processamento para o restante mecanismo de tratamento de erros.
    /// </summary>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        // Escolhe o estado HTTP e uma mensagem adequada ao tipo de falha.
        var (status, title) = exception switch
        {
            // 400: os dados recebidos falharam as regras de validação da aplicação.
            ValidacaoException => (400, "Os dados contêm erros de validação."),
            // 409: a operação entra em conflito com uma configuração já existente.
            ConflitoException => (409, exception.Message),
            // Os códigos SQL Server 2601 e 2627 indicam uma violação de unicidade.
            // Esta proteção também cobre criações simultâneas para o mesmo cliente.
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } =>
                (409, "Já existe uma próxima descarga para este cliente. Recarrega os dados antes de tentar novamente."),
            // 502: falhou a chamada HTTP à StocksAPI ou a leitura do JSON devolvido.
            HttpRequestException or JsonException => (502, "Não foi possível obter dados da StocksAPI. Verifica o endereço e a API key."),
            // 504: houve um cancelamento, por exemplo por timeout, enquanto o pedido
            // do consumidor continua ativo; distingue-se do abandono do próprio pedido.
            OperationCanceledException when !httpContext.RequestAborted.IsCancellationRequested =>
                (504, "A StocksAPI demorou demasiado tempo a responder."),
            // 503: as opções necessárias para contactar a StocksAPI estão em falta ou inválidas.
            OptionsValidationException => (503, "Configura StocksApi:BaseUrl e StocksApi:ApiKey no servidor."),
            // Falhas não previstas recebem uma mensagem genérica, sem expor detalhes internos.
            _ => (500, "Ocorreu um erro ao processar o pedido.")
        };

        // Se o consumidor já cancelou o pedido, não tenta escrever uma resposta para essa ligação.
        if (httpContext.RequestAborted.IsCancellationRequested)
            return false;

        // Guarda os detalhes técnicos das falhas do servidor nos logs para diagnóstico.
        if (status >= 500)
            logger.LogError(exception, "Falha ao processar {Path}", httpContext.Request.Path);

        // ProblemDetails fornece os campos comuns do erro; traceId permite relacioná-lo
        // com os registos de diagnóstico do mesmo pedido.
        var problem = new ProblemDetails { Status = status, Title = title, Instance = httpContext.Request.Path };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        // Inclui os erros de validação por linha para orientar a correção dos dados recebidos.
        if (exception is ValidacaoException validation)
            problem.Extensions["erros"] = validation.Erros;

        // Escreve o JSON com o estado escolhido e o tipo de conteúdo próprio de ProblemDetails.
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: ct);
        // Informa o middleware de que a exceção já foi tratada e a resposta foi produzida.
        return true;
    }
}
