# PolymarketL2DataHarvesterShowcase

### Motivation and Idea

I am a third (and last) year CS student who recently got interested in quantitative finance and what markets are about in general - not because those topics make sense to me but because they don't. For the last few months I have really been interested in the topic and I have been reading about it and watching videos about it for fun in my free time. I saw that, even though a lot of mathematics about randomness and games of incomplete information are publicly available, when it comes to the spicy stuff around it a lot of the information is confusing or even wrong - why would anyone destroy their edge by posting it? This is why for the last few weeks I have tried deploying my skills not just to read about markets, but to feel them in real time. I started working on research examining the efficiency of this type of market, as it is a win-win idea - I either end up proving they are efficient in certain aspects (most likely) or I end up with a trading bot (less likely, high upside potential).

Now, prediction markets are markets where you can buy a Yes/No share for under 1 USD about a real-life event (e.g., "Will Trump say banana next week?") and you can either sell the shares or wait for the event to resolve, at which point a correct share pays 1 USD and an incorrect one 0. What prediction markets call negative risk markets are markets that revolve around a question which can have exactly one winning outcome, and a No share of outcome A is equivalent to a bundle of Yes shares for all outcomes other than A. What I am tracking and eventually writing research on is those moments when you can buy all outcomes for less than 1 USD or sell all outcomes for more than 1 USD (you can mint a complete set for a dollar). What I am also tracking in real time is how the arbitrage disappears once market friction is accounted for (insufficient volume/depth and taker fees).

I did not find a good tool which offers me level 2 data, which I do need to account for the reality of the situation and what would happen if I tried to take advantage of such "top-of-the-book miracles", so I had to build my own.

### Overview of architecture

For this project, I decided to use a Router-Dealer architecture I learned in OS class. Since the WebSocket connection pushes more messages than a single-threaded sequential system can keep up with, I made an `ActiveMarketsManager` (Router) that ingests the raw JSON messages, detects the `negRisk` events, and deals each one to an isolated thread/state-manager. This keeps everything non-blocking and stops my system from calculating EV on stale prices.

As I mentioned, I have an open WebSocket connection - I do not do a GET request every time I want to actually see the prices. I manage the state of the book dynamically as I get the deltas from the connection.

Before ever flagging positive EV, my engine takes into account two things - order book depth and taker fees (Polymarket docs have a page on taker fees). I walk the book so that I can see what the actual Volume-Weighted Average Price (VWAP) is. Then I account for the taker fees, where the fees are `numOfShares × rate × P × (1 − P)`, where the rate is specified depending on the event type and P is Implied Probability/Price.

While all of this is happening, I have logs that describe the current run of the engine, and I write every order book snapshot to `.csv` whenever the top of book seems mispriced, while making sure all threads exit safely before termination of the program so that I don't lose any data.

### Current Results and Future Development

In the repository you can see an example `.csv` file and logs (`LOGS.txt`) that I currently get. I don't feel comfortable sharing everything I found here, even though I have not consistently left it running for more than a few hours and my goal is 2 weeks. This is because I am still working out how to manage the volume of data without running out of memory (needed ~2 hrs to fill up my laptop with gigabytes of data the first time I tried, it is better now but not good enough) and making sure my engine tracks only the interesting markets and does not waste time. Also, the example CSV in this repo has a known logging bug (for BID-side arbitrages) where the executable price/fee fields are occasionally left blank on export - the pricing/fee logic itself is unaffected, this is a serialization issue I haven't chased down yet.

Eventually, those results will most likely turn into research. In the not-so-likely event of discovering consistent mispricings, those results will turn into a trading bot - this means making sure I account for all market friction, possibly rewriting everything in a lower-level language and getting a VPS in Dublin (servers are in London, but all UK traffic is redirected due to regulatory reasons).

I have not shared the complete source code in this public showcase repository and I probably will not do it soon. The `ActiveMarketsManager.cs` module is included here to demonstrate system architecture, thread isolation, and event routing. Full access to the private repository can be provided to hiring managers or technical interviewers upon request.

### Worked Example: Real Detected Arbitrage (WV-02 House Election, Aug 2026)

The row below is a real, unmodified snapshot from an actual detection event - not a synthetic or cherry-picked test case.

| Timestamp | ArbType | BidSum | AskSum | EffectiveAskSum | TotalFee | ArbExistsAfterVWAP | ArbExistsAfterVWAPAndFees |
|---|---|---|---|---|---|---|---|
| 2026-08-10T17:59:42.9176850Z | ASK | 0.962 | 0.968 | 0.968 | 0.0031984 | True | True |

**What this demonstrates:**
This is a complementary-outcome arb on a two-party election market (Republican vs. Democrat, exactly one must win). At the moment of detection, buying "Yes" on both outcomes cost 96.8¢ combined for a guaranteed $1 payout at resolution - a theoretical 3.2¢/dollar edge before fees.

The engine didn't stop at the top-of-book price. It walked both order books simultaneously, summing executable depth level by level, and confirmed the edge survived real VWAP slippage and the platform's effective fee (`TotalFee`) - not just the advertised headline rate. (`EffectiveAskSum` is the depth-walked VWAP sum evaluated at the minimum executable size, which falls inside the first price level here, so it coincides with the top-of-book `AskSum`.) This is the core thing the project is built to check: whether an edge is real after execution friction, or a mirage that only exists at the top of the book.

**Order book snapshot at time of detection** (price → size, cheapest first):

*Republican - Yes (Ask)*

| Price | Size |
|---|---|
| 0.942 | 218.46 |
| 0.943 | 542.08 |
| 0.945 | 20 |
| 0.946 | 20 |
| 0.950 | 20 |
| 0.952 | 319.69 |
| 0.953 | 199.52 |
| 0.955 | 58.46 |
| 0.962 | 2083.39 |
| 0.963 | 126.23 |

*Democrat - Yes (Ask)*

| Price | Size |
|---|---|
| 0.026 | 117 |
| 0.027 | 54 |
| 0.029 | 25.61 |
| 0.067 | 40 |
| 0.068 | 25 |
| 0.122 | 195 |
| 0.123 | 10.28 |
| 0.125 | 5.7 |
| 0.580 | 400 |
| 0.581 | 240 |

This is exactly why the edge is depth-limited: the Republican side is comfortably deep, with 760 contracts resting at or below 94.3¢, so it never becomes the binding constraint within the profitable range. It is the Democrat side that binds - only 196.61 contracts are available below 3¢ before the book jumps to 6.7¢ and then to 12.2¢. Walking through those jumps is what caps the profitable size.

<details>
<summary>Raw BidBookJson / AskBookJson (as logged by the engine)</summary>

```json
// BidBookJson: [Republican-Yes bids, Democrat-Yes bids]
[{"0.901": 199.99, "0.918": 217.86, "0.922": 70, "0.926": 800, "0.928": 353.27, "0.931": 2721.76, "0.934": 20, "0.935": 3019.99, "0.937": 637.28, "0.938": 3218.81}, {"0.001": 22427, "0.002": 1999, "0.003": 800, "0.007": 50, "0.012": 50, "0.016": 150, "0.022": 111, "0.024": 55}]

// AskBookJson: [Republican-Yes asks, Democrat-Yes asks]
[{"0.942": 218.46, "0.943": 542.08, "0.945": 20, "0.946": 20, "0.95": 20, "0.952": 319.69, "0.953": 199.52, "0.955": 58.46, "0.962": 2083.39, "0.963": 126.23}, {"0.026": 117, "0.027": 54, "0.029": 25.61, "0.067": 40, "0.068": 25, "0.122": 195, "0.123": 10.28, "0.125": 5.7, "0.58": 400, "0.581": 240}]
```

</details>

**Depth-limited size:** walking the full book from this snapshot, the position is profitable from approx. 10 contracts up to approx. 345, peaking at approx. 197 contracts (approx. $190 notional) at approximately **$6.16 of gross locked-in profit** (approx. $5.50 after fees), then decaying to zero as thinner price levels get consumed. The peak sits precisely where the Democrat 2.9¢ level is exhausted and the book jumps to 6.7¢. This confirms the edge is real but small - capped by the actual resting liquidity on the book, not by the theoretical spread.

**Risk, for completeness:** this is not risk-free capital. Beyond standard market risk, it carries platform/smart-contract risk, oracle/resolution dispute risk, and custody risk for the ~month until the Nov 3, 2026 resolution - none of which are priced into the raw spread.

### License

Copyright (c) 2026 Petar Zhelev. All Rights Reserved.

This repository is published for demonstration and portfolio assessment purposes only.
No permission or license is granted to use, reproduce, modify, or distribute any part of this software for personal, academic, or commercial use.
