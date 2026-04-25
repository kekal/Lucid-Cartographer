namespace LucidCartographer.Services.Import;

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

                // A ""real"" list card contains a place anchor (the thing the
                // user clicks to open the detail view). We prefer containers
                // whose children actually host such anchors so we don't pick
                // an outer wrapper whose children are section groups rather
                // than individual places.
                function hasPlaceAnchor(el) {
                    return !!el.querySelector(""a[href*='/maps/place/']"");
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
                        if (c.length < MIN_CARDS) continue;
                        // Count how many of the card children actually host a
                        // place anchor — that's the signal we want to maximize.
                        const withAnchor = c.filter(hasPlaceAnchor).length;
                        candidates.push({ el, cards: c, anchorCount: withAnchor, total: c.length });
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

                    // Prefer containers whose children host place anchors.
                    // Fall back to raw child count only when no container
                    // has anchors at all (defensive — shouldn't happen on
                    // a well-formed list URL).
                    const anyWithAnchors = candidates.some(c => c.anchorCount >= MIN_CARDS);
                    let ranked;
                    if (anyWithAnchors) {
                        ranked = candidates.filter(c => c.anchorCount >= MIN_CARDS);
                        ranked.sort((a, b) =>
                            b.anchorCount - a.anchorCount ||
                            a.el.getElementsByTagName('*').length - b.el.getElementsByTagName('*').length
                        );
                    } else {
                        ranked = candidates.slice();
                        ranked.sort((a, b) =>
                            b.total - a.total ||
                            a.el.getElementsByTagName('*').length - b.el.getElementsByTagName('*').length
                        );
                    }

                    const best = ranked[0];
                    container = best.el;
                    // Keep only the children that actually look like place
                    // cards when we have anchor info; otherwise keep all.
                    cards = anyWithAnchors ? best.cards.filter(hasPlaceAnchor) : best.cards;
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

    /// <summary>
    /// Harvest pass: reads every card tagged by Discover and returns their
    /// visible data as an array. Replaces the old click-through strategy —
    /// instead of paying a full detail-page navigation per item just to get
    /// lat/lon from the URL bar, we read the place anchor's href (which
    /// already embeds `@lat,lon,zoom,…`) and let the C# side parse it.
    ///
    /// Fields returned per card:
    ///   idx           data-scraper-idx (int)
    ///   name          aria-label on the place anchor, or fallback
    ///   href          place anchor href (abs URL, contains coords)
    ///   rating        Google rating from span.MW4etd (as text), or null
    ///   reviewCount   raw text from span.UY7F9, or null (digits parsed in C#)
    ///   category      first non-trivial line from .W4Efsd, or null
    ///   description   second non-trivial line from .W4Efsd, or null
    ///   imageSrc      first img[src] on the card, or null
    ///
    /// Anything missing (address / website / phone) is deliberately left
    /// out — the background enrichment service fills those by opening the
    /// place URL in its own headless tab.
    /// </summary>
    public const string HarvestAll = @"
            (() => {
                const cards = Array.from(document.querySelectorAll('[data-scraper-idx]'));
                return cards.map(card => {
                    const idx = parseInt(card.getAttribute('data-scraper-idx'), 10);

                    // Name + href from the place anchor. aria-label is
                    // screen-reader mandated so it's the most stable source.
                    let name = null;
                    let href = null;
                    let a = card.querySelector(""a[href*='/maps/place/']"");
                    if (!a) a = card.querySelector(""a[href*='google.com/maps']"");
                    if (a) {
                        href = a.href || null;
                        const al = a.getAttribute('aria-label');
                        if (al && al.trim()) name = al.trim();
                    }
                    if (!name) {
                        const nameEl = card.querySelector('.fontHeadlineSmall, .qBF1Pd, .NrDZNb');
                        if (nameEl && nameEl.innerText) name = nameEl.innerText.trim();
                    }
                    if (!name) {
                        name = (card.innerText || '').split('\n')[0].trim() || null;
                    }

                    // Rating
                    let rating = null;
                    const rEl = card.querySelector('span.MW4etd');
                    if (rEl && rEl.innerText) rating = rEl.innerText.trim();

                    // Review count (raw text, digit-extraction happens in C#)
                    let reviewCount = null;
                    const rcEl = card.querySelector('span.UY7F9');
                    if (rcEl && rcEl.innerText) reviewCount = rcEl.innerText.trim();

                    // Category + description from .W4Efsd (can repeat; each
                    // block contains bullet-separated lines). Filter out
                    // the name, blank lines, and pure-numeric rating junk.
                    let category = null;
                    let description = null;
                    const bodyEls = Array.from(card.querySelectorAll('.W4Efsd'));
                    for (const el of bodyEls) {
                        const text = (el.innerText || '').trim();
                        if (!text || text === name) continue;
                        const lines = text.split('\n').map(l => l.trim()).filter(l => l);
                        for (const raw of lines) {
                            const clean = raw.replace(/^[·•\s]+|[·•\s]+$/g, '').trim();
                            if (clean.length < 2 || clean === name) continue;
                            // Skip pure rating/review spans like '4.3(1,250)' or '★★★'
                            if (/^[\d.,\s()★]+$/.test(clean)) continue;
                            if (category === null) category = clean;
                            else if (description === null && clean !== category) description = clean;
                        }
                        if (category && description) break;
                    }

                    // First image on the card — used to fetch thumbnail bytes
                    // via APIRequest in C# (we upsize ?=w92-h92-k-no to =w1024).
                    let imageSrc = null;
                    const img = card.querySelector('img');
                    if (img && img.src) imageSrc = img.src;

                    return { idx, name, href, rating, reviewCount, category, description, imageSrc };
                });
            })()";

    /// <summary>
    /// Extracts saved list cards from the Google Maps "Saved → Lists" panel.
    /// Uses stable DOM structure: each list card is a <c>button</c> whose
    /// inner <c>div > div:nth-child(2) > div:first-child</c> holds the name
    /// and <c>div > div:nth-child(2) > div:nth-child(3)</c> holds the count text.
    /// Tags each card with <c>data-savedlist-idx</c> for click-through.
    /// Returns a JSON array of { idx, name, count }.
    /// </summary>
    public const string DiscoverSavedLists = @"
            (() => {
                const results = [];
                const placeRx = /(\d+)/;

                // List card buttons have class 'CsEnBe' in current Google Maps DOM.
                // Each contains: div > div:nth-child(2) > div:first-child (name)
                //                div > div:nth-child(2) > div:nth-child(3) (count text)
                const buttons = document.querySelectorAll('button.CsEnBe');
                let idx = 0;

                for (const btn of buttons) {
                    const nameEl = btn.querySelector('div > div:nth-child(2) > div:first-child');
                    const countEl = btn.querySelector('div > div:nth-child(2) > div:nth-child(3)');

                    const name = nameEl ? nameEl.innerText.trim() : '';
                    if (!name) continue;

                    let count = null;
                    if (countEl) {
                        const m = countEl.innerText.match(placeRx);
                        if (m) count = parseInt(m[1]);
                    }

                    btn.setAttribute('data-savedlist-idx', String(idx));
                    results.push({ idx, name, count });
                    idx++;
                }

                return results;
            })()";
}