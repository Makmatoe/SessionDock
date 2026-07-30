# No reviewed, platform-tested digest is currently recorded; see the supply-chain note in README.md.
FROM node:24-alpine

WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --omit=dev

COPY src ./src

USER node

CMD ["node", "src/index.js"]
