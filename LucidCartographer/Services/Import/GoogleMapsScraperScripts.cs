namespace LucidCartographer.Services.Import
{
    /// <summary>
    /// Browser-side scripts used by <see cref="GoogleMapsListScraper"/>. Extracted
    /// into a standalone class so unit tests can execute the same JS against
    /// fixture HTML without spinning up the full scraper pipeline.
    /// </summary>
    public static class GoogleMapsScraperScripts
    {
        /// <summary>
        /// Discovery pass: finds the list panel by DOM topology (the div with the
        /// most card-like repeating children), tags it with <c>data-scraper-scroll</c>
        /// and each card with <c>data-scraper-idx</c>. Idempotent — re-running only
        /// assigns indices to untagged cards, so existing handles stay valid across
        /// scroll passes and navigations.
        ///
        /// Returns a JSON object:
        ///   <c>{ count, total, scrollFound, divsExamined, diag? }</c>
        /// where <c>count</c> is newly tagged, <c>total</c> is all tags, and
        /// <c>diag</c> (failure only) is the top-5 content-heavy divs.
        /// </summary>
        public const string Discover = @"
            (() => {
                const MIN_CHILD_HEIGHT = 30;
                const MIN_CHILD_TEXT = 5;
                const MIN_CARDS = 3;

                function cardChildren(container) {
                    let candidates = Array.from(container.children);
                    // Unwrap a single spacer
                    if (candidates.length === 1 && candidates[0].children.length > 2) {
                        candidates = Array.from(candidates[0].children);
                    }
                    return candidates.filter(c =>
                        c.offsetHeight >= MIN_CHILD_HEIGHT &&
                        c.innerText &&
                        c.innerText.trim().length >= MIN_CHILD_TEXT
                    );
                }

                let container = document.querySelector(""[data-scraper-scroll='1']"");
                let cards = [];

                if (container) {
                    cards = cardChildren(container);
                } else {
                    const candidates = [];
                    const divs = document.querySelectorAll('div');
                    for (const el of divs) {
                        if (el.children.length < MIN_CARDS && (el.children.length !== 1 || el.children[0].children.length < MIN_CARDS)) continue;
                        const c = cardChildren(el);
                        if (c.length >= MIN_CARDS) {
                            candidates.push({ el, cards: c, score: c.length });
                        }
                    }

                    if (candidates.length === 0) {
                        const diag = [];
                        document.querySelectorAll('div').forEach(el => {
                            const kids = el.children.length;
                            if (kids >= 2) {
                                diag.push({
                                    tag: el.tagName + '.' + (el.className || '').toString().slice(0, 40),
                                    kids,
                                    height: el.offsetHeight,
                                    textLen: (el.innerText || '').length
                                });
                            }
                        });
                        diag.sort((a, b) => b.textLen - a.textLen);
                        return {
                            count: 0,
                            total: 0,
                            scrollFound: false,
                            divsExamined: divs.length,
                            diag: diag.slice(0, 5)
                        };
                    }

                    // Highest score first; tie-break by innermost subtree so we
                    // don't pick the <body>.
                    candidates.sort((a, b) => b.score - a.score || a.el.getElementsByTagName('*').length - b.el.getElementsByTagName('*').length);

                    const best = candidates[0];
                    container = best.el;
                    cards = best.cards;
                    container.setAttribute('data-scraper-scroll', '1');
                }

                // Merge with any existing tags — only assign indices to untagged
                // cards so handles stay valid across discovery calls.
                let maxIdx = -1;
                document.querySelectorAll('[data-scraper-idx]').forEach(el => {
                    const n = parseInt(el.getAttribute('data-scraper-idx'), 10);
                    if (!isNaN(n) && n > maxIdx) maxIdx = n;
                });
                let next = maxIdx + 1;
                let newlyTagged = 0;
                cards.forEach(card => {
                    if (!card.hasAttribute('data-scraper-idx')) {
                        card.setAttribute('data-scraper-idx', String(next++));
                        newlyTagged++;
                    }
                });

                return {
                    count: newlyTagged,
                    total: document.querySelectorAll('[data-scraper-idx]').length,
                    scrollFound: true,
                    divsExamined: document.querySelectorAll('div').length
                };
            })()";
    }
}
