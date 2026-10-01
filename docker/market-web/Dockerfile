FROM node:22-bookworm-slim
WORKDIR /app
COPY market-web/package.json market-web/package-lock.json ./
RUN npm ci
COPY market-web/ ./
EXPOSE 5173
CMD ["sh", "-c", "npm ci && npm run dev"]
