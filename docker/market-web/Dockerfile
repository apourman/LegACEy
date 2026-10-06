# The BFF's development image (docker/docker-compose.local.yml): the website's own server with live reload, the source bind-mounted at /app.
# It runs as the image's node user (uid 1000), so what the dev server writes into the bind mount (React Router's generated route types in
# .react-router/) belongs to the developer, not root.
FROM node:22-bookworm-slim
WORKDIR /app
RUN chown node:node /app
USER node
COPY --chown=node:node market-web/package.json market-web/package-lock.json ./
RUN npm ci
COPY --chown=node:node market-web/ ./
EXPOSE 5173
CMD ["sh", "-c", "npm ci && npm run dev"]
