namespace LucidCartographer.Services.Enrichment
{
    public readonly record struct EnrichmentMetricsSnapshot(
        long AddressFound,
        long PhoneFound,
        long WebsiteFound,
        long SelectorMisses);

    public static class EnrichmentMetrics
    {
        private static long _addressFound;
        private static long _phoneFound;
        private static long _websiteFound;
        private static long _selectorMisses;

        public static void RecordAddressFound() => Interlocked.Increment(ref _addressFound);
        public static void RecordPhoneFound() => Interlocked.Increment(ref _phoneFound);
        public static void RecordWebsiteFound() => Interlocked.Increment(ref _websiteFound);
        public static void RecordSelectorMiss() => Interlocked.Increment(ref _selectorMisses);

        public static EnrichmentMetricsSnapshot Snapshot() => new(
            Interlocked.Read(ref _addressFound),
            Interlocked.Read(ref _phoneFound),
            Interlocked.Read(ref _websiteFound),
            Interlocked.Read(ref _selectorMisses));

        public static EnrichmentMetricsSnapshot Diff(EnrichmentMetricsSnapshot start, EnrichmentMetricsSnapshot end) => new(
            end.AddressFound - start.AddressFound,
            end.PhoneFound - start.PhoneFound,
            end.WebsiteFound - start.WebsiteFound,
            end.SelectorMisses - start.SelectorMisses);
    }
}
