import express from "express";
import {getConfigVariable} from "./util.js";
import FireflyService from "./FireflyService.js";
import OpenAiService from "./OpenAiService.js";
import MistralService from "./MistralService.js";
import {Server} from "socket.io";
import * as http from "http";
import Queue from "queue";
import JobList from "./JobList.js";
import {MissingEnvironmentVariableException} from "./util.js";

export default class App {
    static #VALID_TRANSACTION_TRIGGERS = ["STORE_TRANSACTION", "TRIGGER_STORE_TRANSACTION", "UPDATE_TRANSACTION", "TRIGGER_UPDATE_TRANSACTION"];
    static #VALID_TRANSACTION_RESPONSES = ["TRANSACTIONS", "RESPONSE_TRANSACTIONS"];

    #PORT;
    #ENABLE_UI;

    #firefly;
    //#openAi;
    #mistral;

    #server;
    #io;
    #express;

    #queue;
    #jobList;


    constructor() {
        this.#PORT = getConfigVariable("PORT", '3000');
        this.#ENABLE_UI = getConfigVariable("ENABLE_UI", 'false') === 'true';
    }

    async run() {
        this.#queue = new Queue({
            timeout: 30 * 1000,
            concurrency: 1,
            autostart: true
        });

        this.#queue.addEventListener('start', event => console.log('Job started', event.detail.job))
        this.#queue.addEventListener('success', event => console.log('Job success', event.detail.job))
        this.#queue.addEventListener('error', event => console.error('Job error', event.detail.job, event.detail.error))
        this.#queue.addEventListener('timeout', event => console.log('Job timeout', event.detail.job))

        this.#express = express();
        this.#server = http.createServer(this.#express)
        this.#io = new Server(this.#server)

        this.#jobList = new JobList();
        this.#jobList.on('job created', data => this.#io.emit('job created', data));
        this.#jobList.on('job updated', data => this.#io.emit('job updated', data));

        this.#express.use(express.json());

        if (this.#ENABLE_UI) {
            this.#express.use('/', express.static('public'))
        } else {
            this.#express.get('/', (req, res) => {
                res.status(200).type('text/plain').send('Web UI is disabled. Set ENABLE_UI=true and restart the application to serve the UI at /.');
            });
        }

        this.#express.get('/api/diagnostics', this.#onDiagnostics.bind(this))
        this.#express.post('/webhook', this.#onWebhook.bind(this))

        this.#server.listen(this.#PORT, async () => {
            console.log(`Application running on port ${this.#PORT}`);
        });

        this.#io.on('connection', socket => {
            console.log('connected');
            socket.emit('jobs', Array.from(this.#jobList.getJobs().values()));
        })
    }

    #getFireflyService() {
        if (!this.#firefly) {
            this.#firefly = new FireflyService();
        }

        return this.#firefly;
    }

    #getMistralService() {
        if (!this.#mistral) {
            this.#mistral = new MistralService();
        }

        return this.#mistral;
    }

    async #onDiagnostics(req, res) {
        const diagnostics = await Promise.all([
            this.#runCheck('firefly', async () => await this.#getFireflyService().diagnose()),
            this.#runCheck('mistral', async () => await this.#getMistralService().diagnose())
        ]);

        const ok = diagnostics.every(check => check.ok);
        res.status(ok ? 200 : 503).json({
            ok,
            checks: diagnostics
        });
    }

    async #runCheck(name, fn) {
        try {
            const details = await fn();
            return {
                name,
                ok: true,
                details
            };
        } catch (error) {
            return {
                name,
                ok: false,
                error: error instanceof MissingEnvironmentVariableException
                    ? `Missing environment variable: ${error.variableName}`
                    : error.message
            };
        }
    }

    #onWebhook(req, res) {
        try {
            console.info("Webhook triggered");
            this.#handleWebhook(req, res);
            res.send("Queued");
        } catch (e) {
            console.error(e)
            res.status(400).send(e.message);
        }
    }

    #handleWebhook(req, res) {
        // TODO: validate auth

        if (!App.#VALID_TRANSACTION_TRIGGERS.includes(req.body?.trigger)) {
            throw new WebhookException(`Unsupported webhook trigger "${req.body?.trigger}". Expected one of: ${App.#VALID_TRANSACTION_TRIGGERS.join(", ")}.`);
        }

        if (!App.#VALID_TRANSACTION_RESPONSES.includes(req.body?.response)) {
            throw new WebhookException(`Unsupported webhook response "${req.body?.response}". Expected one of: ${App.#VALID_TRANSACTION_RESPONSES.join(", ")}.`);
        }

        if (!req.body?.content?.id) {
            throw new WebhookException("Missing content.id");
        }

        if (req.body?.content?.transactions?.length === 0) {
            throw new WebhookException("No transactions are available in content.transactions");
        }

        if (req.body.content.transactions[0].type !== "withdrawal") {
            throw new WebhookException("content.transactions[0].type has to be 'withdrawal'. Transaction will be ignored.");
        }

        if (req.body.content.transactions[0].category_name !== null) {
            throw new WebhookException("content.transactions[0].category_id is already set. Transaction will be ignored.");
        }

        if (!req.body.content.transactions[0].description) {
            throw new WebhookException("Missing content.transactions[0].description");
        }

        if (!req.body.content.transactions[0].destination_name) {
            throw new WebhookException("Missing content.transactions[0].destination_name");
        }

        const destinationName = req.body.content.transactions[0].destination_name;
        const description = req.body.content.transactions[0].description

        const job = this.#jobList.createJob({
            destinationName,
            description
        });

        this.#queue.push(async () => {
            this.#jobList.setJobInProgress(job.id);

            const firefly = this.#getFireflyService();
            const mistral = this.#getMistralService();
            const categories = await firefly.getCategories();
            const budgets = await firefly.getBudgets();

            const allLists = new Map();

            allLists.set('categories', Array.from(categories.keys()));
            allLists.set('budgets', Array.from(budgets.keys()));

            const {prompt, category, budget, response} = await mistral.classify(allLists, destinationName, description)

            const newData = Object.assign({}, job.data);
            newData.category = category;
            newData.budget = budget;
            newData.prompt = prompt;
            newData.response = response;

            this.#jobList.updateJobData(job.id, newData);

            if (category || budget) {
                const category_id = categories.has(category) ? categories.get(category) : -1;
                const budget_id = budgets.has(budget) ? budgets.get(budget) : -1;
                await firefly.setCategoryAndBudget(req.body.content.id, req.body.content.transactions, category_id, budget_id);
            }

            this.#jobList.setJobFinished(job.id);
        });
    }
}

class WebhookException extends Error {

    constructor(message) {
        super(message);
    }
}
