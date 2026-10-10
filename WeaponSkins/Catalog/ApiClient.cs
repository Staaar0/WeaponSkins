using System.Net;
using System.Text.Json;

namespace WeaponSkins;

public sealed class ApiClient : IDisposable
{
	private readonly string? baseUrl;
	private readonly string? language;
	private readonly string? dataDirectory;
	private readonly HttpClient? http;
	private readonly TimeSpan requestTimeout;

	public ApiClient(ApiConfig config)
	{
		baseUrl = config.BaseUrl.TrimEnd('/');
		language = string.IsNullOrWhiteSpace(config.Language) ? "en" : config.Language;
		requestTimeout = TimeSpan.FromSeconds(Math.Max(5, config.TimeoutSeconds));
		// Compress the large catalog download.
		http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
		{
			Timeout = Timeout.InfiniteTimeSpan
		};
		http.DefaultRequestHeaders.UserAgent.ParseAdd("WeaponSkins");
	}

	public ApiClient(string dataDirectory)
	{
		this.dataDirectory = dataDirectory;
	}

	public async Task<JsonDocument> Fetch(string file, CancellationToken cancellationToken = default)
	{
		if (dataDirectory != null)
		{
			await using var local = File.OpenRead(Path.Combine(dataDirectory, $"{file}.json"));
			return await JsonDocument.ParseAsync(local, cancellationToken: cancellationToken);
		}

		// Keep the timeout active while reading and parsing the response.
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(requestTimeout);
		using var response = await http!.GetAsync($"{baseUrl}/{language}/{file}.json",
			HttpCompletionOption.ResponseHeadersRead, deadline.Token);
		response.EnsureSuccessStatusCode();
		await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
		return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
	}

	public void Dispose()
	{
		http?.Dispose();
	}
}
