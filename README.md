# PolymarketL2DataHarvesterShowcase

### Motivation and Idea 

I am about to be a third (and last) year CS student, who recently got interested in quantitative finance and what markets are about in general - not because those topics make sense to me but because they don't. For the last few months I have really been interested in the topic and I have been reading about it and watching videos about it for fun in my free time. I saw that, even though a lot of mathematics about randomness and games of incomplete information are publically available, when it comes to the spicy stuff around it a lot of the information about it is confusing or even wrong (includes some published research papers on prediction markets) - why would anyone destroy their edge by posting it. This is why for the last few weeks I have tried deploying my skills to get away from just reading about markets but to feel them real-time. I started working on research which argues about the efficiency of this type of markets, as it is a win-win idea - I either end up proving they are efficient in certain aspects (most likely) or I end up with a trading bot (less likely, high upside potential).

Now, prediction markets are markets where you can buy a Yes/No share for under 1 USD answering a question around a real-life event (e.g "Will Trump say banana next week") and you can either sell the shares or wait for the event to terminate for which you either get 1 USD for correct share and 0 otherwise. What prediciton markets call negative risk markets are markets that revolve around a question which can have exactly 1 outcome and a No share of outcome A is equivalent to a bundle of Yes shares for all outcomes other than A. What I am tracking and eventually writing research on is those moments when you can buy all outcomes for less than 1 USD or sell all outcomes for more than 1 USD (you can mint a complete set for a dollar). What I am also tracking real-time is how the arbitrage disappears when accounted for market friction (insufficient volume/depth and taker fees).

I did not find a good tool which offers me level 2 data, which I do need to account for the reality of the situation and what would happen if I try taking advantage of such "top-of-the-book miracles", so I had to build my own.

### Overview of architecture

For this project, I decided to use a Router-Dealer architecture I learned in OS class. Since the WebSocket connection shoots too many messages for a mere 1 threaded sequential system to manage without lagging behind a lot, I made an ActivaMakretsManager (Router) that ingests the raw JSON messages which detects the negRisk events and deals each one to an isolated thread/state-manager. This keeps everything non-blocking and stops my system form calculating EV on stale prices.

As I mentioned, I have an open WebSocket connection - I do not do a GET request every time I want to actually see the prices. I manage the state of the book dynamically as I get the deltas from the connection. 

Before ever flagging positive EV my engine takes into account 2 things - orderbook depth and taker fees (Polymarket docs have a page on taker fees). I walk the book so that I can see what the actual Volume-Weighted Average Price (VWAP) is. Then I account for the taker fees, where the fees are numOfShares x rate x P x (1-P), where the rate is specified depending on the event type and P is Implied Probability/Price.

While all of this is happening, I have logs that describe the current run of the engine and I write all snapshots of the orderbook when top-of-the-book seems mispriced to .csv files, while making sure all threads exit safely before termination of the program and everything closes safely so that I dont lose any data. 

### Current Results and Future Development
In the repository you can see an example .csv file and logs (LOGS.txt) that I currently get. I don't feel comfortable sharing here everything I found, even though I have not consistently left it running for more than few hours and my goal is 2 weeks. This is due to still working on how to manage the big ammount of data without running out of memory (needed ~2 hrs to fill up my laptop with gigabytes of data the first time I tried, it is better now but not good enoguh) and making sure my engine tracks the interesting markets only and does not waste time. Also, the example CSV in this repo has a known logging bug (for BID-side arbitrages) where the executable price/fee fields are occasionally left blank on export — the pricing/fee logic itself is unaffected, this is a serialization issue I haven't chased down yet.

Eventually, those results will most likely turn into research. In the not so likely event of discovering consistent mispricings, those results will turn into a trading bot - this means making sure I account for all market friction, possibly rewriting everything in a lower level language and getting a VPS in Dublin (servers are in London, but all UK trafic is redirected due to regulatory reasons).

I have not shared the complete source code in this public showcase repository and I probably will not do it soon. The ActiveMarketsManager.cs module is included here to demonstrate system architecture, thread isolation, and event routing. Full access to the private repository can be provided to hiring managers or technical interviewers upon request. 

##### License

Copyright (c) 2026 Petar Zhelev. All Rights Reserved.

This repository is published for demonstration and portfolio assessment purposes only. 
No permission or license is granted to use, reproduce, modify, or distribute any part of this software for personal, academic, or commercial use.
