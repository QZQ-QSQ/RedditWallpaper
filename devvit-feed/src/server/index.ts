import { Hono } from "hono";
import { reddit } from "@devvit/web/server";

const app = new Hono();

type FeedItem = {
  id: string;
  title: string;
  url: string;
  score: number;
  subreddit: string;
};

const IMAGE_RE = /\.(jpe?g|png)$/i;

/**
 * GET /external/feed
 * 供桌面壁纸程序调用（需 External Endpoint 权限 + Managed Token）：
 *   ?subs=EarthPorn,wallpapers&sort=hot&t=week&limit=100&minScore=100&nsfw=1
 *
 * 由 Reddit 一侧直接调用 Reddit API（context.reddit），因此不受客户端 IP 被
 * .json 端点封锁的影响，同时保留了 score / NSFW 过滤能力。
 */
app.get("/external/feed", async (c) => {
  const subsParam = c.req.query("subs") ?? "EarthPorn";
  const subs = subsParam
    .split(",")
    .map((s) => s.trim().replace(/^r\//i, ""))
    .filter(Boolean)
    .slice(0, 10);

  const sort = c.req.query("sort") ?? "hot";
  const timeframe = c.req.query("t") ?? "week";
  const limit = Math.min(200, Math.max(1, Number(c.req.query("limit") ?? 100)));
  const minScore = Math.max(0, Number(c.req.query("minScore") ?? 0));
  const skipNsfw = (c.req.query("nsfw") ?? "1") !== "0";

  const items: FeedItem[] = [];
  const errors: string[] = [];

  for (const sub of subs) {
    try {
      const options = { subredditName: sub, limit } as const;

      // Devvit 客户端没有 best/controversial 方法，回退到 hot
      const supportedSort = sort === "top" || sort === "new" || sort === "rising" || sort === "hot";
      const effectiveSort = supportedSort ? sort : "hot";
      if (!supportedSort) errors.push(`${sort}: Devvit 不支持，已回退 hot`);

      const listing =
        effectiveSort === "top"
          ? reddit.getTopPosts({ ...options, timeframe } as never)
          : effectiveSort === "new"
            ? reddit.getNewPosts(options as never)
            : effectiveSort === "rising"
              ? reddit.getRisingPosts(options as never)
              : reddit.getHotPosts(options as never);

      const posts = await listing.all();

      for (const post of posts) {
        const url = (post.url ?? "").split("?")[0];
        if (!IMAGE_RE.test(url)) continue;
        if (skipNsfw && post.nsfw) continue;
        if (minScore > 0 && (post.score ?? 0) < minScore) continue;

        items.push({
          id: post.id,
          title: post.title ?? "",
          url,
          score: post.score ?? 0,
          subreddit: sub,
          permalink: post.permalink ?? "",
        });
      }
    } catch (error) {
      errors.push(`${sub}: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

  return c.json({
    items,
    errors,
    count: items.length,
    generatedAt: new Date().toISOString(),
  });
});

export default app;
