using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OpenPixel.Services
{
    // ========================================================================
    // Real quote lookup for the Stock Prices widget.
    //
    // Uses Yahoo Finance's public chart endpoint (no key, no account). Every
    // failure path leaves the widget showing "—" rather than inventing a
    // price: an offline PC must never display a fabricated number.
    // ========================================================================
    public sealed class StockQuote
    {
        public string Symbol { get; init; } = "";
        public double Price { get; init; }
        public double PreviousClose { get; init; }
        public bool Valid { get; init; }
        public string Currency { get; init; } = "";

        public double ChangePercent =>
            Valid && PreviousClose > 0.0001 ? (Price - PreviousClose) / PreviousClose * 100.0 : 0.0;
    }

    public static class StockQuotes
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<string, StockQuote> _quotes = new(StringComparer.OrdinalIgnoreCase);
        private static long _lastRefreshMs;
        private static bool _refreshInFlight;
        private static string _lastError = "";

        public static string LastError => _lastError;

        public static bool TryGet(string symbol, out StockQuote quote)
        {
            lock (Sync) return _quotes.TryGetValue(symbol ?? "", out quote);
        }

        public static bool IsStale => Environment.TickCount64 - _lastRefreshMs > 60000;

        // Fire-and-forget refresh; never blocks the render thread.
        public static void Refresh(IReadOnlyList<string> symbols)
        {
            if (symbols == null || symbols.Count == 0) return;
            bool start;
            lock (Sync)
            {
                if (_refreshInFlight) return;
                if (Environment.TickCount64 - _lastRefreshMs < 55000) return;
                _refreshInFlight = true;
                start = true;
            }
            if (!start) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenPixel/1.0");
                    foreach (string raw in symbols)
                    {
                        string symbol = (raw ?? "").Trim();
                        if (symbol.Length == 0) continue;
                        StockQuote quote = await FetchAsync(http, symbol).ConfigureAwait(false);
                        if (quote == null) continue;
                        lock (Sync) _quotes[symbol] = quote;
                    }
                    lock (Sync) _lastRefreshMs = Environment.TickCount64;
                    _lastError = "";
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name;
                }
                finally
                {
                    lock (Sync) _refreshInFlight = false;
                }
            });
        }

        private static async Task<StockQuote> FetchAsync(HttpClient http, string symbol)
        {
            try
            {
                string url = "https://query1.finance.yahoo.com/v8/finance/chart/" +
                             Uri.EscapeDataString(symbol) + "?range=1d&interval=1d";
                string json = await http.GetStringAsync(url).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement result = doc.RootElement
                    .GetProperty("chart").GetProperty("result");
                if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;

                JsonElement meta = result[0].GetProperty("meta");
                double price = meta.TryGetProperty("regularMarketPrice", out JsonElement p) ? p.GetDouble() : 0.0;
                double prev = meta.TryGetProperty("chartPreviousClose", out JsonElement c) ? c.GetDouble()
                            : (meta.TryGetProperty("previousClose", out JsonElement c2) ? c2.GetDouble() : 0.0);
                string currency = meta.TryGetProperty("currency", out JsonElement cu) ? cu.GetString() ?? "" : "";

                if (price <= 0.0) return null;
                return new StockQuote
                {
                    Symbol = symbol.ToUpperInvariant(),
                    Price = price,
                    PreviousClose = prev,
                    Currency = currency,
                    Valid = true
                };
            }
            catch
            {
                return null;
            }
        }

        public static string FormatPrice(StockQuote quote)
        {
            if (quote == null || !quote.Valid) return "\u2014";
            string number = quote.Price >= 1000.0
                ? quote.Price.ToString("N0", CultureInfo.InvariantCulture)
                : quote.Price.ToString("0.00", CultureInfo.InvariantCulture);
            return quote.Currency == "USD" || quote.Currency.Length == 0 ? "$" + number : number;
        }
    }
}
