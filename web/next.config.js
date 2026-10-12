// Set NEXT_PUBLIC_BASE_PATH (e.g. "/Ai") at build time to serve the UI under a sub-path.
// basePath is baked into the build; Link/router/asset URLs pick it up automatically.
const basePath = process.env.NEXT_PUBLIC_BASE_PATH || "";

/** @type {import('next').NextConfig} */
const nextConfig = {
  output: "standalone",
  reactStrictMode: true,
  ...(basePath && { basePath })
};

module.exports = nextConfig;
