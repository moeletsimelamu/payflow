# PayFlow dashboard: Angular build served by unprivileged nginx (runs as a non-root user on 8080).
FROM node:22.14.0-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
# Install scripts are not needed for the build and are not run.
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY . .
RUN npx ng build --configuration production

FROM nginxinc/nginx-unprivileged:1.28-alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/payflow-dashboard/browser /usr/share/nginx/html
EXPOSE 8080
